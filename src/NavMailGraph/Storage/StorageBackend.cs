using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Storage
{
    /// <summary>Result of an upload (for GraphMailbox.CopyAttachmentToStorage: LastStorageKey/Size/ETag).</summary>
    internal sealed class StorageUploadResult
    {
        internal StorageUploadResult(string key, long size, string eTag, int parts)
        {
            Key = key;
            Size = size;
            ETag = eTag;
            Parts = parts;
        }

        /// <summary>Full object key including KeyPrefix.</summary>
        internal string Key { get; }

        internal long Size { get; }

        internal string ETag { get; }

        /// <summary>0 = single PUT, otherwise number of blocks/parts.</summary>
        internal int Parts { get; }
    }

    /// <summary>Body of a single PUT: either a byte range of a buffer or a range of a seekable stream (replayable for retries).</summary>
    internal sealed class UploadBody
    {
        private UploadBody(byte[]? buffer, int count, Stream? stream, long start, long length)
        {
            Buffer = buffer;
            Count = count;
            Stream = stream;
            Start = start;
            Length = buffer != null ? count : length;
        }

        internal byte[]? Buffer { get; }

        internal int Count { get; }

        internal Stream? Stream { get; }

        internal long Start { get; }

        internal long Length { get; }

        internal static UploadBody FromBuffer(byte[] buffer, int count)
        {
            return new UploadBody(buffer, count, null, 0, count);
        }

        internal static UploadBody FromSeekableStream(Stream stream, long start, long length)
        {
            return new UploadBody(null, 0, stream, start, length);
        }

        /// <summary>A fresh HttpContent for one attempt (seekable streams are rewound to <see cref="Start"/>).</summary>
        internal HttpContent CreateContent()
        {
            HttpContent content;
            if (Buffer != null)
            {
                content = new ByteArrayContent(Buffer, 0, Count);
            }
            else
            {
                Stream!.Seek(Start, SeekOrigin.Begin);
                content = new StreamContent(new NonClosingStream(Stream, Length), StorageHttp.CopyBufferSize);
            }

            content.Headers.ContentLength = Length;
            return content;
        }
    }

    /// <summary>State of a running block/multipart upload.</summary>
    internal sealed class MultipartState
    {
        internal MultipartState(string key, string contentType, string uploadId)
        {
            Key = key;
            ContentType = contentType;
            UploadId = uploadId;
        }

        internal string Key { get; }

        internal string ContentType { get; }

        /// <summary>S3 UploadId ("" for Azure).</summary>
        internal string UploadId { get; }

        /// <summary>Block ids (Azure) or ETags (S3) by ascending part number.</summary>
        internal List<string> Parts { get; } = new List<string>();

        /// <summary>Total bytes of all parts, set before completion (-1 while unknown).</summary>
        internal long TotalBytes { get; set; } = -1;
    }

    /// <summary>Settings snapshot taken at the start of every operation.</summary>
    internal sealed class StorageSettings
    {
        internal long MultipartThresholdBytes { get; set; }

        internal int PartSizeBytes { get; set; }

        internal int TimeoutSeconds { get; set; }

        internal int MaxRetries { get; set; }
    }

    /// <summary>
    /// Provider-neutral operations plus the shared upload algorithm. Azure: Put Blob / Put Block + Put Block List;
    /// S3: PutObject / multipart upload.
    /// </summary>
    internal abstract class StorageBackend
    {
        protected StorageBackend(StorageHttp http, StorageSettings settings, Func<DateTime> clock)
        {
            Http = http;
            Settings = settings;
            Clock = clock;
        }

        internal StorageHttp Http { get; }

        internal StorageSettings Settings { get; }

        internal Func<DateTime> Clock { get; }

        /// <summary>Maximum number of parts/blocks.</summary>
        internal abstract int MaxParts { get; }

        /// <summary>Minimum size of every part except the last.</summary>
        internal abstract int MinPartSize { get; }

        internal abstract string Describe();

        internal abstract Task<string> PutAsync(string key, string contentType, UploadBody body, CancellationToken cancellationToken);

        internal abstract Task<MultipartState> BeginMultipartAsync(string key, string contentType, CancellationToken cancellationToken);

        /// <summary>Uploads one part (1-based) and records its id/ETag in <paramref name="state"/>.Parts.</summary>
        internal abstract Task UploadPartAsync(MultipartState state, int partNumber, byte[] buffer, int count, CancellationToken cancellationToken);

        internal abstract Task<string> CompleteMultipartAsync(MultipartState state, CancellationToken cancellationToken);

        /// <summary>Best effort, never throws.</summary>
        internal abstract Task AbortMultipartAsync(MultipartState state);

        /// <summary>Streams the object into <paramref name="target"/>; returns the byte count.</summary>
        internal abstract Task<long> DownloadAsync(string key, Stream target, CancellationToken cancellationToken);

        /// <summary>Object size, -1 when it does not exist.</summary>
        internal abstract Task<long> GetSizeAsync(string key, CancellationToken cancellationToken);

        /// <summary>Deletes; a missing object is success.</summary>
        internal abstract Task DeleteAsync(string key, CancellationToken cancellationToken);

        internal abstract Task<ListPage> ListPageAsync(string prefix, string marker, int maxResults, CancellationToken cancellationToken);

        /// <summary>Time-limited read URL (throws a validation error when the mode cannot create one).</summary>
        internal abstract string CreateDownloadUrl(string key, int validSeconds);

        /// <summary>Single PUT limit actually used (MultipartThresholdBytes, clamped).</summary>
        internal long Threshold
        {
            get
            {
                long t = Settings.MultipartThresholdBytes;
                return t > StorageKeys.MaxSinglePutBytes ? StorageKeys.MaxSinglePutBytes : t;
            }
        }

        /// <summary>
        /// Uploads <paramref name="source"/> from its current position. Seekable sources up to the threshold go out as one
        /// streamed PUT (replayable for retries); everything else is read part by part into one reused buffer: when the
        /// source ends inside the first part it becomes a single PUT from that buffer, otherwise Put Block/List or multipart.
        /// Works for non-seekable sources of unknown length (<paramref name="knownLength"/> = -1). A known length is verified.
        /// </summary>
        internal async Task<StorageUploadResult> UploadAsync(Stream source, long knownLength, string key, string contentType, CancellationToken cancellationToken)
        {
            long length = knownLength;
            if (source.CanSeek)
            {
                try
                {
                    length = source.Length - source.Position;
                }
                catch (NotSupportedException)
                {
                    length = knownLength;
                }
            }

            if (source.CanSeek && length >= 0 && length <= Threshold)
            {
                string eTag = await PutAsync(key, contentType, UploadBody.FromSeekableStream(source, source.Position, length), cancellationToken).ConfigureAwait(false);
                return new StorageUploadResult(key, length, eTag, 0);
            }

            int partSize = StorageKeys.EffectivePartSize(length, Settings.PartSizeBytes, MaxParts, MinPartSize);
            if (partSize < 0)
            {
                throw Http.Validation("upload", key, "the object is too large for " + MaxParts + " parts of at most " + (StorageKeys.MaxPartBufferBytes / StorageKeys.MiB) + " MiB");
            }

            byte[] buffer = new byte[partSize];
            int first = await ReadFullyAsync(source, buffer, partSize, key, cancellationToken).ConfigureAwait(false);
            if (first < partSize)
            {
                CheckLength(key, knownLength, first);
                string eTag = await PutAsync(key, contentType, UploadBody.FromBuffer(buffer, first), cancellationToken).ConfigureAwait(false);
                return new StorageUploadResult(key, first, eTag, 0);
            }

            MultipartState state = await BeginMultipartAsync(key, contentType, cancellationToken).ConfigureAwait(false);
            bool completing = false;
            try
            {
                long total = 0;
                int count = first;
                int partNumber = 1;
                while (true)
                {
                    await UploadPartAsync(state, partNumber, buffer, count, cancellationToken).ConfigureAwait(false);
                    total += count;
                    if (count < partSize)
                    {
                        break;
                    }

                    count = await ReadFullyAsync(source, buffer, partSize, key, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    if (partNumber >= MaxParts)
                    {
                        throw Http.Validation("upload", key, "the source is larger than " + MaxParts + " parts of " + partSize + " bytes; increase PartSizeBytes");
                    }

                    partNumber++;
                }

                CheckLength(key, knownLength, total);
                state.TotalBytes = total;
                completing = true;
                string eTag = await CompleteMultipartAsync(state, cancellationToken).ConfigureAwait(false);
                Http.Log.Log("multipart upload complete: " + partNumber + " parts, " + total + " bytes");
                return new StorageUploadResult(key, total, eTag, partNumber);
            }
            catch (Exception ex)
            {
                var failure = ex as GraphMailException;
                if (completing && failure != null && failure.ErrorCode == GraphMailException.TransportCode)
                {
                    // Timeout or lost connection while completing: the service may still be assembling (or have created) the object,
                    // so the parts are kept; an incomplete upload is cleaned up by the bucket's lifecycle rule / the service.
                    Http.Log.Log("completion outcome unknown after a transport error; the upload is not aborted");
                    throw;
                }

                await AbortMultipartAsync(state).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Content type with a parsed (normalized) value; throws a validation error for malformed input.</summary>
        internal MediaTypeHeaderValue ParseContentType(string key, string contentType)
        {
            MediaTypeHeaderValue? parsed;
            if (!MediaTypeHeaderValue.TryParse(contentType, out parsed) || parsed == null)
            {
                throw Http.Validation("upload", key, "invalid content type '" + contentType + "'");
            }

            return parsed;
        }

        private void CheckLength(string key, long knownLength, long actual)
        {
            if (knownLength >= 0 && knownLength != actual)
            {
                throw Http.Validation("upload", key, "the source delivered " + actual + " bytes but " + knownLength + " were expected");
            }
        }

        private async Task<int> ReadFullyAsync(Stream source, byte[] buffer, int count, string key, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, total, count - total, cancellationToken).ConfigureAwait(false);
                }
                catch (GraphMailException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ObjectDisposedException || ex is HttpRequestException)
                {
                    throw GraphMailException.Validation(StorageHttp.Redact(Http.Provider + " upload failed: the source could not be read after " + total + " bytes of this part (" + ex.Message + ") [" + Http.Target + ", key " + key + "]"), ex);
                }

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
    }

    /// <summary>Read-only window over a seekable stream that never closes it (StreamContent disposes its stream).</summary>
    internal sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;

        private long _remaining;

        internal NonClosingStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

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
            if (_remaining <= 0)
            {
                return 0;
            }

            int read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            int read = await _inner.ReadAsync(buffer, offset, (int)Math.Min(count, _remaining), cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
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
            // the caller owns the inner stream
        }
    }
}
