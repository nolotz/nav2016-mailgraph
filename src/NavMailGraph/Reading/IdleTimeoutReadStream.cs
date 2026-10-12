using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;

namespace NavMailGraph.Reading
{
    /// <summary>
    /// Read-only, forward-only wrapper around a Graph $value response stream that is piped into an object storage upload.
    /// The Graph idle timeout must only count time spent waiting for Graph data, not time spent uploading a buffered part
    /// to storage: the wrapper resets the idle timer before and after every read, and a keep-alive timer resets it
    /// periodically while no read is in progress. A read that stalls longer than the idle timeout still fires it.
    /// Seek, Length, Position and Write are not supported (the storage side treats the source as unknown length).
    /// </summary>
    internal sealed class IdleTimeoutReadStream : Stream
    {
        private readonly Stream _inner;

        private readonly Action _reset;

        private readonly Timer _keepAlive;

        private readonly string _what;

        private int _reading;

        private int _disposed;

        internal IdleTimeoutReadStream(Stream inner, Action resetIdleTimeout, TimeSpan keepAlivePeriod, string what)
        {
            _what = what ?? "";
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _reset = resetIdleTimeout ?? throw new ArgumentNullException(nameof(resetIdleTimeout));
            if (keepAlivePeriod <= TimeSpan.Zero)
            {
                keepAlivePeriod = TimeSpan.FromSeconds(1);
            }

            _keepAlive = new Timer(OnKeepAlive, null, keepAlivePeriod, keepAlivePeriod);
        }

        /// <summary>Bytes read so far.</summary>
        internal long BytesRead { get; private set; }

        /// <summary>
        /// The Graph-side read failure (as a Transport <see cref="GraphMailException"/>), null when every read succeeded. Read
        /// errors are thrown as this exception, so the consumer can tell a Graph failure from a storage failure.
        /// </summary>
        internal GraphMailException? ReadFailure { get; private set; }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BeginRead();
            try
            {
                int read = _inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }
            catch (Exception ex) when (!(ex is GraphMailException))
            {
                throw Failed(ex);
            }
            finally
            {
                EndRead();
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            BeginRead();
            try
            {
                int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                BytesRead += read;
                return read;
            }
            catch (Exception ex) when (!(ex is GraphMailException))
            {
                throw Failed(ex);
            }
            finally
            {
                EndRead();
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // The inner (response) stream is owned by the Graph download; only the timer is ours.
                _keepAlive.Dispose();
            }

            base.Dispose(disposing);
        }

        private GraphMailException Failed(Exception ex)
        {
            string reason = ex is OperationCanceledException || ex is ObjectDisposedException
                ? "the download was aborted (no data within the timeout, or canceled)"
                : GraphClient.DescribeTransportError(ex);
            ReadFailure = new GraphMailException(
                "Microsoft Graph download of " + _what + " failed after " + BytesRead + " bytes: " + reason,
                GraphMailException.TransportCode, 0, "", true, ex);
            return ReadFailure;
        }

        private void BeginRead()
        {
            SafeReset();
            Interlocked.Exchange(ref _reading, 1);
        }

        private void EndRead()
        {
            Interlocked.Exchange(ref _reading, 0);
            SafeReset();
        }

        private void OnKeepAlive(object? state)
        {
            if (Volatile.Read(ref _reading) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                SafeReset();
            }
        }

        private void SafeReset()
        {
            try
            {
                _reset();
            }
            catch (ObjectDisposedException)
            {
                // The download already finished or was aborted.
            }
        }
    }
}
