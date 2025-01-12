﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.

namespace Nerdbank.Streams
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipelines;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;

    /// <summary>
    /// A <see cref="PipeReader"/> that reads from an underlying <see cref="Stream"/> exactly when told to do so
    /// rather than constantly reading from the stream and buffering up the results.
    /// </summary>
    internal class StreamPipeReader : PipeReader
    {
        private readonly object syncObject = new object();

        private readonly Stream stream;

        private readonly int bufferSize;

        private readonly Sequence<byte> buffer = new Sequence<byte>();

        private SequencePosition examined;

        private CancellationTokenSource? readCancellationSource;

        private bool isReaderCompleted;

        private Exception? readerException;

        private bool isWriterCompleted;

        private Exception? writerException;

        private List<(Action<Exception?, object?>, object?)>? writerCompletedCallbacks;

        internal StreamPipeReader(Stream stream, int bufferSize)
        {
            Requires.NotNull(stream, nameof(stream));
            Requires.Argument(stream.CanRead, nameof(stream), "Stream must be readable.");
            this.stream = stream;
            this.bufferSize = bufferSize;
        }

        /// <inheritdoc />
        public override void AdvanceTo(SequencePosition consumed) => this.AdvanceTo(consumed, consumed);

        /// <inheritdoc />
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            lock (this.syncObject)
            {
                this.buffer.AdvanceTo(consumed);
                this.examined = examined;
            }
        }

        /// <inheritdoc />
        public override void CancelPendingRead() => this.readCancellationSource?.Cancel();

        /// <inheritdoc />
        public override void Complete(Exception? exception = null)
        {
            lock (this.syncObject)
            {
                this.isReaderCompleted = true;
                this.readerException = exception;
                this.buffer.Reset();
            }
        }

        /// <inheritdoc />
        public override void OnWriterCompleted(Action<Exception?, object?> callback, object? state)
        {
            Requires.NotNull(callback, nameof(callback));

            bool invokeNow;
            lock (this.syncObject)
            {
                if (this.isWriterCompleted)
                {
                    invokeNow = true;
                }
                else
                {
                    invokeNow = false;
                    if (this.writerCompletedCallbacks == null)
                    {
                        this.writerCompletedCallbacks = new List<(Action<Exception?, object?>, object?)>();
                    }

                    this.writerCompletedCallbacks.Add((callback, state));
                }
            }

            if (invokeNow)
            {
                callback(this.writerException, state);
            }
        }

        /// <inheritdoc />
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (this.TryRead(out ReadResult result))
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (this.readCancellationSource?.IsCancellationRequested ?? true)
            {
                this.readCancellationSource = new CancellationTokenSource();
            }

            Memory<byte> memory;
            lock (this.syncObject)
            {
                memory = this.buffer.GetMemory(this.bufferSize);
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.readCancellationSource!.Token))
            {
                try
                {
                    int bytesRead = await this.stream.ReadAsync(memory, cts.Token).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        this.CompleteWriting();
                        return new ReadResult(this.buffer, isCanceled: false, isCompleted: true);
                    }

                    lock (this.syncObject)
                    {
                        this.buffer.Advance(bytesRead);
                        return new ReadResult(this.buffer, isCanceled: false, isCompleted: false);
                    }
                }
                catch (OperationCanceledException) when (this.readCancellationSource.Token.IsCancellationRequested)
                {
                    return new ReadResult(this.buffer, isCanceled: true, isCompleted: this.isReaderCompleted);
                }
            }
        }

        /// <inheritdoc />
        public override bool TryRead(out ReadResult result)
        {
            lock (this.syncObject)
            {
                Verify.Operation(!this.isReaderCompleted, "Reading is already completed.");

                if (this.buffer.AsReadOnlySequence.Length > 0 && !this.buffer.AsReadOnlySequence.End.Equals(this.examined))
                {
                    result = new ReadResult(this.buffer, isCanceled: false, isCompleted: this.isWriterCompleted);
                    return true;
                }

                result = default;
                return false;
            }
        }

        private void CompleteWriting(Exception? writerException = null)
        {
            List<(Action<Exception?, object?>, object?)>? writerCompletedCallbacks = null;
            lock (this.syncObject)
            {
                if (!this.isWriterCompleted)
                {
                    this.isWriterCompleted = true;
                    this.writerException = writerException;

                    writerCompletedCallbacks = this.writerCompletedCallbacks;
                    this.writerCompletedCallbacks = null;
                }
            }

            if (writerCompletedCallbacks != null)
            {
                foreach (var callback in writerCompletedCallbacks)
                {
                    try
                    {
                        callback.Item1(writerException, callback.Item2);
                    }
                    catch
                    {
                        // Swallow each exception.
                    }
                }
            }
        }
    }
}
