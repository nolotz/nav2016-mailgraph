using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;
using NavMailGraph.Internal;
using NavMailGraph.Storage;

namespace NavMailGraph
{
    /// <summary>
    /// Object storage for NAV: Azure Blob Storage (Shared Key or SAS URL) and Amazon S3 / S3-compatible storage (SigV4).
    /// Streamed uploads (single PUT up to <see cref="MultipartThresholdBytes"/>, Put Block/List or S3 multipart above),
    /// streamed downloads, delete, exists, size, list and time-limited download links.
    /// C/AL-friendly: every operation returns "" on success or a complete error text (also in <see cref="LastError"/>) and never
    /// throws for remote, IO or validation problems. Secrets never appear in error texts, <see cref="LastDiagnostics"/> or <see cref="Describe"/>.
    /// Not thread-safe: use one instance per NAV session/job.
    /// </summary>
    public sealed class ObjectStorageClient
    {
        /// <summary>Default MultipartThresholdBytes: 64 MiB.</summary>
        public const long DefaultMultipartThresholdBytes = 64L * 1024 * 1024;

        /// <summary>Default PartSizeBytes: 8 MiB.</summary>
        public const int DefaultPartSizeBytes = 8 * 1024 * 1024;

        /// <summary>Default TimeoutSeconds: 300.</summary>
        public const int DefaultTimeoutSeconds = 300;

        /// <summary>Default MaxRetries: 3.</summary>
        public const int DefaultMaxRetries = 3;

        /// <summary>Default x-ms-version (fully deployed in all public regions, accepted by Azurite 3.37.0).</summary>
        public const string DefaultAzureApiVersion = "2026-04-06";

        /// <summary>Default Azure blob endpoint suffix.</summary>
        public const string DefaultBlobEndpointSuffix = "blob.core.windows.net";

        /// <summary>Blob endpoint suffix of Azure China (21Vianet).</summary>
        internal const string ChinaBlobEndpointSuffix = "blob.core.chinacloudapi.cn";

        private const string DefaultS3Region = "us-east-1";

        private const int DefaultListPageSize = 1000;

        private readonly Diagnostics _diag = new Diagnostics();

        private readonly HttpClient? _httpClient;

        private readonly Func<DateTime> _clock;

        private readonly List<ListedObject> _listed = new List<ListedObject>();

        private Provider _provider = Provider.None;

        private string _configError = "";

        private IStorageAuth? _azureAuth;

        private string _accountName = "";

        private string _container = "";

        private string _sasContainerUrl = "";

        private string _s3Region = DefaultS3Region;

        private string _s3Bucket = "";

        private string _s3AccessKeyId = "";

        private string _s3SecretAccessKey = "";

        private bool? _s3PathStyle;

        private int _timeoutSeconds = DefaultTimeoutSeconds;

        private string _logFilePath = "";

        /// <summary>Creates an unconfigured client; call exactly one of the Use* methods.</summary>
        public ObjectStorageClient()
            : this(null, null, null)
        {
        }

        /// <summary>Test/integration constructor: own HTTP client, clock and retry delay.</summary>
        internal ObjectStorageClient(HttpClient? httpClient, Func<DateTime>? clock, Func<TimeSpan, CancellationToken, Task>? retryDelay)
        {
            HttpClientProvider.EnsureInitialized();
            _httpClient = httpClient;
            _clock = clock ?? (() => DateTime.UtcNow);
            RetryDelayOverride = retryDelay;
        }

        private enum Provider
        {
            None,
            Azure,
            S3,
        }

        // ------------------------------------------------------------------------------------------------------------
        // Configuration
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Azure Blob Storage with the storage account key (Shared Key). The key is the base64 key1/key2 value.</summary>
        public void UseAzureBlobSharedKey(string accountName, string accountKey, string container)
        {
            Reset();
            _provider = Provider.Azure;
            _accountName = (accountName ?? "").Trim();
            _container = (container ?? "").Trim();
            string key = (accountKey ?? "").Trim();
            if (_accountName.Length == 0 || _container.Length == 0 || key.Length == 0)
            {
                _configError = "Azure Blob configuration incomplete: account name, account key and container are required.";
                return;
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(key);
            }
            catch (FormatException)
            {
                _configError = "Azure Blob configuration invalid: the account key is not valid base64 (copy key1 or key2 of the storage account exactly).";
                return;
            }

            _azureAuth = new AzureSharedKeyAuth(keyBytes);
        }

        /// <summary>
        /// Azure Blob Storage with a container SAS URL (https://acct.blob.core.windows.net/container?sv=...&amp;sig=...).
        /// Every request uses this SAS; <see cref="GetDownloadUrl"/> returns the blob URL with the same SAS.
        /// </summary>
        public void UseAzureBlobSasUrl(string containerSasUrl)
        {
            Reset();
            _provider = Provider.Azure;
            string url = (containerSasUrl ?? "").Trim();
            Uri? uri;
            int q = url.IndexOf('?');
            if (q <= 0 || q == url.Length - 1 || !Uri.TryCreate(url.Substring(0, q), UriKind.Absolute, out uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                _configError = "Azure Blob configuration invalid: the SAS URL must look like https://<account>.blob.core.windows.net/<container>?sv=...&sig=...";
                return;
            }

            string sas = url.Substring(q + 1);
            if (sas.IndexOf("sig=", StringComparison.Ordinal) < 0)
            {
                _configError = "Azure Blob configuration invalid: the SAS URL has no signature (sig=...).";
                return;
            }

            string path = StorageKeys.PathOf(url.Substring(0, q)).TrimEnd('/');
            string[] segments = path.Trim('/').Split('/');
            if (segments.Length == 0 || segments[segments.Length - 1].Length == 0)
            {
                _configError = "Azure Blob configuration invalid: the SAS URL contains no container name.";
                return;
            }

            _container = Uri.UnescapeDataString(segments[segments.Length - 1]);
            // Azurite/IP style: http://127.0.0.1:10000/devstoreaccount1/container; otherwise the first host label is the account.
            _accountName = segments.Length >= 2 ? Uri.UnescapeDataString(segments[segments.Length - 2]) : uri!.Host.Split('.')[0];
            _sasContainerUrl = uri!.Scheme + "://" + uri.Authority + path;
            _azureAuth = new AzureSasAuth(sas);
        }

        /// <summary>
        /// Amazon S3 or an S3-compatible service. <paramref name="serviceUrl"/> "" = AWS (https://s3.{region}.amazonaws.com);
        /// <paramref name="region"/> "" = us-east-1.
        /// </summary>
        public void UseS3(string serviceUrl, string region, string bucket, string accessKeyId, string secretAccessKey)
        {
            Reset();
            _provider = Provider.S3;
            ServiceUrl = (serviceUrl ?? "").Trim();
            _s3Region = string.IsNullOrWhiteSpace(region) ? DefaultS3Region : region.Trim();
            _s3Bucket = (bucket ?? "").Trim();
            _s3AccessKeyId = (accessKeyId ?? "").Trim();
            _s3SecretAccessKey = secretAccessKey ?? "";
            if (_s3Bucket.Length == 0 || _s3AccessKeyId.Length == 0 || _s3SecretAccessKey.Length == 0)
            {
                _configError = "S3 configuration incomplete: bucket, access key id and secret access key are required.";
                return;
            }

            Uri? endpoint;
            if (ServiceUrl.Length > 0 && (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out endpoint) || (endpoint.Scheme != "https" && endpoint.Scheme != "http") || endpoint.Query.Length > 0))
            {
                _configError = "S3 configuration invalid: the service URL must be an http(s) URL without query, e.g. https://s3.eu-central-1.amazonaws.com.";
            }
        }

        /// <summary>
        /// Azure Blob Storage with an Entra ID token of the app registration configured in <paramref name="connection"/> (client secret
        /// or certificate; token scope https://storage.azure.com/.default). The app needs the RBAC role "Storage Blob Data Contributor"
        /// (or Reader for read-only use) on the storage account or container. The connection's tenant, client, credential, cloud,
        /// authority host and token library are used; the token is acquired on the first operation and cached process-wide.
        /// Supported clouds: Global (blob.core.windows.net) and China (blob.core.chinacloudapi.cn); for other endpoints set
        /// <see cref="ServiceUrl"/>. <see cref="GetDownloadUrl"/> is not available in this mode (needs Shared Key or a SAS URL).
        /// A connection that uses <see cref="GraphConnection.UseTokenProvider"/> passes that provider through unchanged: it must
        /// return Azure Storage tokens. Throws <see cref="ArgumentNullException"/> only for a null connection.
        /// </summary>
        public void UseAzureBlobEntra(GraphConnection connection, string accountName, string container)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            GraphMailSender settings = connection.Settings;
            CloudEndpoints? endpoints = null;
            string cloudError = "";
            try
            {
                endpoints = settings.ResolveEndpoints();
            }
            catch (GraphMailException ex)
            {
                cloudError = ex.Message;
            }

            if (endpoints != null && !settings.HasExplicitTokenProvider && endpoints != CloudEndpoints.Global && endpoints != CloudEndpoints.China)
            {
                // The Azure Storage token audience of the US Government clouds is not verified (docs/research-notes-v2.md).
                cloudError = "Azure Blob configuration invalid: Entra ID mode supports the clouds Global and China only (connection Cloud="
                    + endpoints.Name + "); use UseAzureBlobSharedKey or UseAzureBlobSasUrl for this cloud.";
            }

            CloudEndpoints resolved = endpoints ?? CloudEndpoints.Global;
            string description = settings.HasExplicitTokenProvider
                ? "provider"
                : "tenant=" + settings.TenantId + " client=" + settings.ClientId + " cloud=" + resolved.Name;
            var auth = new AzureEntraAuth(() => settings.CreateTokenProviderForScope(resolved, AzureEntraAuth.StorageScope), description);
            UseAzureBlobAuth(auth, accountName, container);
            if (resolved == CloudEndpoints.China && string.Equals((BlobEndpointSuffix ?? "").Trim(), DefaultBlobEndpointSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // https://learn.microsoft.com/en-us/azure/storage/common/storage-powershell-independent-clouds
                BlobEndpointSuffix = ChinaBlobEndpointSuffix;
            }

            if (_configError.Length == 0 && cloudError.Length > 0)
            {
                _configError = cloudError;
            }
        }

        /// <summary>
        /// Integration hook for further Azure authorization modes (e.g. Entra ID bearer tokens): configures Azure Blob with
        /// <paramref name="auth"/> and the account endpoint from <see cref="ServiceUrl"/>/<see cref="BlobEndpointSuffix"/>.
        /// </summary>
        internal void UseAzureBlobAuth(IStorageAuth auth, string accountName, string container)
        {
            Reset();
            _provider = Provider.Azure;
            _azureAuth = auth;
            _accountName = (accountName ?? "").Trim();
            _container = (container ?? "").Trim();
            if (_accountName.Length == 0 || _container.Length == 0)
            {
                _configError = "Azure Blob configuration incomplete: account name and container are required.";
            }
        }

        /// <summary>Azure endpoint suffix, default "blob.core.windows.net" (US Gov: blob.core.usgovcloudapi.net, China: blob.core.chinacloudapi.cn).</summary>
        public string BlobEndpointSuffix { get; set; } = DefaultBlobEndpointSuffix;

        /// <summary>
        /// Azure: account URL override (Azurite: http://127.0.0.1:10000/devstoreaccount1); S3: endpoint (set by UseS3).
        /// Ignored in SAS URL mode.
        /// </summary>
        public string ServiceUrl { get; set; } = "";

        /// <summary>
        /// S3 path-style addressing (endpoint/bucket/key). Default: true unless the endpoint is an amazonaws.com host and the bucket
        /// has no dot. Setting the property overrides the default.
        /// </summary>
        public bool S3PathStyle
        {
            get { return _s3PathStyle ?? DefaultPathStyle(); }
            set { _s3PathStyle = value; }
        }

        /// <summary>Session token of temporary (STS) credentials, "" for none.</summary>
        public string S3SessionToken { get; set; } = "";

        /// <summary>Prepended to every object key (e.g. "nav/prod/"); listed keys are returned without it.</summary>
        public string KeyPrefix { get; set; } = "";

        /// <summary>Uploads up to this size use a single PUT; larger ones use Put Block/List or S3 multipart. Default 64 MiB.</summary>
        public long MultipartThresholdBytes { get; set; } = DefaultMultipartThresholdBytes;

        /// <summary>Block/part size, default 8 MiB (S3 minimum 5 MiB; raised automatically when 10,000/50,000 parts would not suffice).</summary>
        public int PartSizeBytes { get; set; } = DefaultPartSizeBytes;

        /// <summary>
        /// Timeout in seconds without progress, default 300: per request until the response headers arrive, restarted by every
        /// 64 KiB of request body sent (slow uplinks do not fail large uploads while data flows); downloads and the S3 multipart
        /// completion response: idle timeout per read.
        /// </summary>
        public int TimeoutSeconds
        {
            get { return _timeoutSeconds; }
            set { _timeoutSeconds = value; }
        }

        /// <summary>Retries for idempotent operations on 408/429/5xx and transport errors, default 3.</summary>
        public int MaxRetries { get; set; } = DefaultMaxRetries;

        /// <summary>
        /// x-ms-version for Azure requests, default 2026-04-06. Account-key SAS links (GetDownloadUrl) always use sv 2020-12-06
        /// (the newest documented service SAS layout) and therefore need AzureApiVersion 2020-12-06 or later.
        /// </summary>
        public string AzureApiVersion { get; set; } = DefaultAzureApiVersion;

        /// <summary>Optional append-only diagnostic log file (secrets redacted).</summary>
        public string LogFilePath
        {
            get { return _logFilePath; }
            set { _logFilePath = value ?? ""; }
        }

        // ------------------------------------------------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>Uploads a file. <paramref name="contentType"/> "" = by file extension.</summary>
        public string UploadFile(string filePath, string objectKey, string contentType)
        {
            return Run("UploadFile", async ct =>
            {
                string path = filePath ?? "";
                if (path.Length == 0 || !File.Exists(path))
                {
                    throw GraphMailException.Validation("Upload failed: file not found: " + path);
                }

                string type = string.IsNullOrEmpty(contentType) ? ContentTypes.FromFileName(path) : contentType;
                FileStream stream;
                try
                {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, StorageHttp.CopyBufferSize, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    throw GraphMailException.Validation("Upload failed: file cannot be read: " + path + " (" + ex.Message + ")", ex);
                }

                using (stream)
                {
                    await UploadCoreAsync(stream, stream.Length, objectKey, type, ct).ConfigureAwait(false);
                }
            });
        }

        /// <summary>Uploads <paramref name="source"/> from its current position to its end. <paramref name="contentType"/> "" = by key extension.</summary>
        public string UploadStream(Stream source, string objectKey, string contentType)
        {
            return Run("UploadStream", async ct =>
            {
                if (source == null || !source.CanRead)
                {
                    throw GraphMailException.Validation("Upload failed: the source stream is missing or not readable.");
                }

                await UploadCoreAsync(source, -1, objectKey, contentType, ct).ConfigureAwait(false);
            });
        }

        /// <summary>Uploads text as UTF-8 (no BOM). <paramref name="contentType"/> "" = text/plain; charset=utf-8.</summary>
        public string UploadText(string text, string objectKey, string contentType)
        {
            return Run("UploadText", async ct =>
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                string type = string.IsNullOrEmpty(contentType) ? "text/plain; charset=utf-8" : contentType;
                using (var stream = new MemoryStream(bytes, false))
                {
                    await UploadCoreAsync(stream, bytes.Length, objectKey, type, ct).ConfigureAwait(false);
                }
            });
        }

        /// <summary>
        /// Downloads an object into a file. The data goes to a temporary file next to it ("{file}.nmgtmp-{guid}") that replaces
        /// the target only after a complete download; on failure an existing file at <paramref name="filePath"/> stays untouched.
        /// </summary>
        public string DownloadToFile(string objectKey, string filePath)
        {
            return Run("DownloadToFile", async ct =>
            {
                string path = filePath ?? "";
                StorageBackend backend = CreateBackend();
                string key = FullKey(backend, objectKey, "download");
                if (path.Trim().Length == 0)
                {
                    throw GraphMailException.Validation("Download failed: the file cannot be created: no file path given");
                }

                string temp = path + ".nmgtmp-" + Guid.NewGuid().ToString("N");
                FileStream file;
                try
                {
                    file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, StorageHttp.CopyBufferSize, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    throw GraphMailException.Validation("Download failed: the file cannot be created: " + path + " (" + ex.Message + ")", ex);
                }

                try
                {
                    long bytes;
                    using (file)
                    {
                        bytes = await backend.DownloadAsync(key, file, ct).ConfigureAwait(false);
                    }

                    ReplaceFile(temp, path);
                    _diag.Log("downloaded " + bytes + " bytes to " + path);
                }
                finally
                {
                    TryDelete(temp);
                }
            });
        }

        /// <summary>Downloads an object into <paramref name="target"/> (written from its current position).</summary>
        public string DownloadToStream(string objectKey, Stream target)
        {
            return Run("DownloadToStream", async ct =>
            {
                if (target == null || !target.CanWrite)
                {
                    throw GraphMailException.Validation("Download failed: the target stream is missing or not writable.");
                }

                StorageBackend backend = CreateBackend();
                long bytes = await backend.DownloadAsync(FullKey(backend, objectKey, "download"), target, ct).ConfigureAwait(false);
                _diag.Log("downloaded " + bytes + " bytes");
            });
        }

        /// <summary>Deletes an object; a missing object counts as success.</summary>
        public string Delete(string objectKey)
        {
            return Run("Delete", async ct =>
            {
                StorageBackend backend = CreateBackend();
                await backend.DeleteAsync(FullKey(backend, objectKey, "delete"), ct).ConfigureAwait(false);
            });
        }

        /// <summary>True when the object exists. False on failure too: then <see cref="LastError"/> is set (it is "" for a plain "not found").</summary>
        public bool Exists(string objectKey)
        {
            long size = -1;
            Run("Exists", async ct =>
            {
                StorageBackend backend = CreateBackend();
                size = await backend.GetSizeAsync(FullKey(backend, objectKey, "exists"), ct).ConfigureAwait(false);
            });
            return LastError.Length == 0 && size >= 0;
        }

        /// <summary>Object size in bytes; -1 when the object does not exist (LastError "") or on failure (LastError set).</summary>
        public long GetSize(string objectKey)
        {
            long size = -1;
            string error = Run("GetSize", async ct =>
            {
                StorageBackend backend = CreateBackend();
                size = await backend.GetSizeAsync(FullKey(backend, objectKey, "size"), ct).ConfigureAwait(false);
            });
            return error.Length == 0 ? size : -1;
        }

        /// <summary>Lists all objects whose key starts with KeyPrefix + <paramref name="prefix"/> (all pages); read with ListCount/GetListedKey/GetListedSize.</summary>
        public string List(string prefix)
        {
            _listed.Clear();
            return Run("List", async ct =>
            {
                StorageBackend backend = CreateBackend();
                string fullPrefix = StorageKeys.Combine(KeyPrefix, prefix);
                string marker = "";
                var items = new List<ListedObject>();
                do
                {
                    ListPage page = await backend.ListPageAsync(fullPrefix, marker, ListPageSize > 0 ? ListPageSize : DefaultListPageSize, ct).ConfigureAwait(false);
                    foreach (ListedObject item in page.Items)
                    {
                        string key = item.Key;
                        string keyPrefix = KeyPrefix ?? "";
                        if (keyPrefix.Length > 0 && key.StartsWith(keyPrefix, StringComparison.Ordinal))
                        {
                            key = key.Substring(keyPrefix.Length);
                        }

                        items.Add(new ListedObject(key, item.Size));
                    }

                    if (page.NextMarker.Length > 0 && page.NextMarker == marker)
                    {
                        throw GraphMailException.Validation("List failed: the service returned the same continuation marker twice.");
                    }

                    marker = page.NextMarker;
                }
                while (marker.Length > 0);

                _listed.AddRange(items);
                _diag.Log("listed " + items.Count + " objects");
            });
        }

        /// <summary>Number of objects found by the last <see cref="List"/>.</summary>
        public int ListCount
        {
            get { return _listed.Count; }
        }

        /// <summary>Key (without KeyPrefix) of the listed object at 0-based <paramref name="index"/>; "" when out of range.</summary>
        public string GetListedKey(int index)
        {
            return index >= 0 && index < _listed.Count ? _listed[index].Key : "";
        }

        /// <summary>Size of the listed object at 0-based <paramref name="index"/>; -1 when out of range.</summary>
        public long GetListedSize(int index)
        {
            return index >= 0 && index < _listed.Count ? _listed[index].Size : -1;
        }

        /// <summary>
        /// Time-limited read link: S3 presigned GET (max 7 days), Azure service SAS (Shared Key) or the blob URL with the configured SAS
        /// (SAS URL mode; validity then comes from the SAS). Returns "" and sets <see cref="LastError"/> when no link can be created.
        /// </summary>
        public string GetDownloadUrl(string objectKey, int validMinutes)
        {
            string url = "";
            Run("GetDownloadUrl", ct =>
            {
                if (validMinutes <= 0)
                {
                    throw GraphMailException.Validation("Download URL failed: validMinutes must be greater than 0.");
                }

                StorageBackend backend = CreateBackend();
                int seconds = (int)Math.Min((long)validMinutes * 60, int.MaxValue);
                url = backend.CreateDownloadUrl(FullKey(backend, objectKey, "download URL"), seconds);
                _diag.Log("download URL created (valid " + validMinutes + " min)");
                return Task.FromResult(0);
            });
            return LastError.Length == 0 ? url : "";
        }

        /// <summary>Checks configuration, credentials and the container/bucket by listing at most one object.</summary>
        public string TestConnection()
        {
            return Run("TestConnection", async ct =>
            {
                StorageBackend backend = CreateBackend();
                _diag.Log(backend.Describe());
                ListPage page = await backend.ListPageAsync(KeyPrefix ?? "", "", 1, ct).ConfigureAwait(false);
                _diag.Log("connection ok (" + page.Items.Count + " object(s) in the first page)");
            });
        }

        /// <summary>Error text of the last operation, "" after success.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>Request id (x-ms-request-id / x-amz-request-id) of the last failed request, for support. "" if none.</summary>
        public string LastRequestId { get; private set; } = "";

        /// <summary>Redacted log of the last operation.</summary>
        public string LastDiagnostics
        {
            get { return _diag.Text; }
        }

        /// <summary>One-line description of the configuration without secrets.</summary>
        public string Describe()
        {
            if (_provider == Provider.None)
            {
                return "ObjectStorageClient (not configured)";
            }

            if (_configError.Length > 0)
            {
                return "ObjectStorageClient (invalid configuration: " + _configError + ")";
            }

            try
            {
                StorageBackend backend = CreateBackend();
                return StorageHttp.Redact(backend.Describe() + (string.IsNullOrEmpty(KeyPrefix) ? "" : " keyPrefix=" + KeyPrefix)
                    + " threshold=" + backend.Threshold + " partSize=" + PartSizeBytes + " timeout=" + EffectiveTimeoutSeconds + "s retries=" + EffectiveMaxRetries);
            }
            catch (GraphMailException ex)
            {
                return "ObjectStorageClient (invalid configuration: " + ex.Message + ")";
            }
        }

        // ------------------------------------------------------------------------------------------------------------
        // Internals (GraphMailbox integration, tests)
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Streams <paramref name="source"/> (seekable or not, known length or -1) to <paramref name="objectKey"/> (KeyPrefix is applied).
        /// Non-seekable sources are buffered one part at a time (one reused buffer). Throws <see cref="GraphMailException"/>.
        /// </summary>
        internal Task<StorageUploadResult> UploadFromStreamAsync(Stream source, long knownLengthOrMinus1, string objectKey, string contentType, CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return UploadCoreAsync(source, knownLengthOrMinus1, objectKey, contentType, cancellationToken);
        }

        /// <summary>
        /// Starts an operation driven by another class (GraphMailbox.CopyAttachmentToStorage, GraphMailSender link storage, storage
        /// attachments): new diagnostics, LastError/LastRequestId cleared. Finish with <see cref="EndExternalOperation"/>.
        /// </summary>
        internal void BeginExternalOperation(string name)
        {
            _diag.LogFilePath = _logFilePath;
            _diag.BeginOperation("ObjectStorage." + name);
            LastRequestId = "";
            LastError = "";
        }

        /// <summary>Records the outcome of an external operation: "" or the exception (which the caller rethrows or reports).</summary>
        internal void EndExternalOperation(Exception? failure)
        {
            if (failure != null)
            {
                LastError = Fail(failure);
            }
        }

        /// <summary>Checks configuration and <paramref name="objectKey"/> (with KeyPrefix) without network; throws the validation error.</summary>
        internal void ValidateForUpload(string objectKey)
        {
            StorageBackend backend = CreateBackend();
            FullKey(backend, objectKey, "upload");
        }

        /// <summary>Read link for <paramref name="objectKey"/> (KeyPrefix applied); throws <see cref="GraphMailException"/> when impossible.</summary>
        internal string CreateDownloadUrlCore(string objectKey, int validMinutes)
        {
            if (validMinutes <= 0)
            {
                throw GraphMailException.Validation("Download URL failed: validMinutes must be greater than 0.");
            }

            StorageBackend backend = CreateBackend();
            int seconds = (int)Math.Min((long)validMinutes * 60, int.MaxValue);
            return backend.CreateDownloadUrl(FullKey(backend, objectKey, "download URL"), seconds);
        }

        /// <summary>Downloads <paramref name="objectKey"/> (KeyPrefix applied) into <paramref name="target"/>; throws on failure.</summary>
        internal Task<long> DownloadCoreAsync(string objectKey, Stream target, CancellationToken cancellationToken)
        {
            StorageBackend backend = CreateBackend();
            return backend.DownloadAsync(FullKey(backend, objectKey, "download"), target, cancellationToken);
        }

        /// <summary>Deletes <paramref name="objectKey"/> (KeyPrefix applied); throws on failure.</summary>
        internal Task DeleteCoreAsync(string objectKey, CancellationToken cancellationToken)
        {
            StorageBackend backend = CreateBackend();
            return backend.DeleteAsync(FullKey(backend, objectKey, "delete"), cancellationToken);
        }

        /// <summary>The key without KeyPrefix (what callers pass to DownloadToFile etc.).</summary>
        internal string StripKeyPrefix(string fullKey)
        {
            string prefix = KeyPrefix ?? "";
            return prefix.Length > 0 && (fullKey ?? "").StartsWith(prefix, StringComparison.Ordinal) ? fullKey!.Substring(prefix.Length) : (fullKey ?? "");
        }

        /// <summary>
        /// Wraps a storage failure for another class's error text (GraphMailbox / GraphMailSender): code "Storage" and status 0,
        /// so their Graph permission hints are not appended to a storage error; the storage hint and request id are kept.
        /// </summary>
        internal static GraphMailException WrapForCaller(Exception exception, string context)
        {
            GraphMailException? known = exception as GraphMailException;
            string detail;
            if (known != null)
            {
                detail = known.Message;
            }
            else if (exception is OperationCanceledException)
            {
                detail = "operation canceled: " + exception.Message;
            }
            else
            {
                detail = "unexpected error (" + exception.GetType().Name + "): " + GraphClient.FirstLine(exception.Message);
            }

            string text = StorageHttp.Redact(context + ": " + detail);
            if (known != null && known.ErrorCode.StartsWith("AADSTS", StringComparison.OrdinalIgnoreCase))
            {
                text = ErrorHints.Append(text, ErrorHints.GetHint(known.ErrorCode, 0, false, known.Message));
            }

            return new GraphMailException(text, StorageErrorCode, 0, known == null ? "" : known.RequestId, known != null && known.IsTransient, exception);
        }

        /// <summary>Error code of wrapped storage failures (see <see cref="WrapForCaller"/>).</summary>
        internal const string StorageErrorCode = "Storage";

        /// <summary>Page size of <see cref="List"/> requests (maxresults / max-keys), default 1000; tests lower it to exercise paging.</summary>
        internal int ListPageSize { get; set; } = DefaultListPageSize;

        /// <summary>Test hook: replaces the retry pause.</summary>
        internal Func<TimeSpan, CancellationToken, Task>? RetryDelayOverride { get; set; }

        internal int EffectiveTimeoutSeconds
        {
            get { return _timeoutSeconds > 0 ? _timeoutSeconds : DefaultTimeoutSeconds; }
        }

        internal int EffectiveMaxRetries
        {
            get { return MaxRetries < 0 ? 0 : MaxRetries; }
        }

        /// <summary>Builds the backend for the current settings (throws a validation error when not configured).</summary>
        internal StorageBackend CreateBackend()
        {
            if (_provider == Provider.None)
            {
                throw GraphMailException.Validation("Object storage is not configured: call UseAzureBlobSharedKey, UseAzureBlobSasUrl, UseAzureBlobEntra or UseS3 first.");
            }

            if (_configError.Length > 0)
            {
                throw GraphMailException.Validation(_configError);
            }

            var settings = new StorageSettings
            {
                MultipartThresholdBytes = MultipartThresholdBytes > 0 ? MultipartThresholdBytes : DefaultMultipartThresholdBytes,
                PartSizeBytes = PartSizeBytes > 0 ? PartSizeBytes : DefaultPartSizeBytes,
                TimeoutSeconds = EffectiveTimeoutSeconds,
                MaxRetries = EffectiveMaxRetries,
            };
            HttpClient http = _httpClient ?? HttpClientProvider.Client;
            Func<TimeSpan, CancellationToken, Task> delay = RetryDelayOverride ?? ((pause, ct) => Task.Delay(pause, ct));

            if (_provider == Provider.Azure)
            {
                string apiVersion = string.IsNullOrWhiteSpace(AzureApiVersion) ? DefaultAzureApiVersion : AzureApiVersion.Trim();
                string containerUrl;
                if (_azureAuth is AzureSasAuth)
                {
                    containerUrl = _sasContainerUrl;
                }
                else
                {
                    string account = string.IsNullOrWhiteSpace(ServiceUrl)
                        ? "https://" + _accountName + "." + (string.IsNullOrWhiteSpace(BlobEndpointSuffix) ? DefaultBlobEndpointSuffix : BlobEndpointSuffix.Trim().Trim('.'))
                        : ServiceUrl.Trim().TrimEnd('/');
                    Uri? accountUri;
                    if (!Uri.TryCreate(account, UriKind.Absolute, out accountUri) || (accountUri.Scheme != "https" && accountUri.Scheme != "http") || accountUri.Query.Length > 0)
                    {
                        throw GraphMailException.Validation("Azure Blob configuration invalid: ServiceUrl must be an http(s) account URL without query.");
                    }

                    containerUrl = account + "/" + StorageKeys.UriEncode(_container, true);
                }

                var storageHttp = new StorageHttp(http, _diag, "Azure Blob", "container " + _container, settings.TimeoutSeconds, settings.MaxRetries, delay);
                return new AzureBlobBackend(storageHttp, settings, _clock, _azureAuth!, _accountName, _container, containerUrl, apiVersion);
            }

            string serviceUrl = string.IsNullOrWhiteSpace(ServiceUrl) ? "https://s3." + _s3Region + ".amazonaws.com" : ServiceUrl.Trim();
            var s3Http = new StorageHttp(http, _diag, "S3", "bucket " + _s3Bucket, settings.TimeoutSeconds, settings.MaxRetries, delay);
            return new S3Backend(s3Http, settings, _clock, serviceUrl, _s3Region, _s3Bucket, S3PathStyle, _s3AccessKeyId, _s3SecretAccessKey, S3SessionToken ?? "");
        }

        private async Task<StorageUploadResult> UploadCoreAsync(Stream source, long knownLength, string objectKey, string contentType, CancellationToken cancellationToken)
        {
            StorageBackend backend = CreateBackend();
            string key = FullKey(backend, objectKey, "upload");
            string type = string.IsNullOrWhiteSpace(contentType) ? ContentTypes.FromFileName(key) : contentType.Trim();
            if (backend is S3Backend && PartSizeBytes > 0 && PartSizeBytes < StorageKeys.S3MinPartSize)
            {
                _diag.Log("PartSizeBytes below the S3 minimum; using 5 MiB parts");
            }

            StorageUploadResult result = await backend.UploadAsync(source, knownLength, key, type, cancellationToken).ConfigureAwait(false);
            _diag.Log("uploaded " + result.Size + " bytes" + (result.Parts > 0 ? " in " + result.Parts + " parts" : " (single PUT)"));
            return result;
        }

        private string FullKey(StorageBackend backend, string objectKey, string operation)
        {
            string key = StorageKeys.Combine(KeyPrefix, objectKey);
            string reason = StorageKeys.Validate(key);
            if (reason.Length > 0)
            {
                throw backend.Http.Validation(operation, key, reason);
            }

            return key;
        }

        private bool DefaultPathStyle()
        {
            if (_s3Bucket.IndexOf('.') >= 0)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(ServiceUrl))
            {
                return false;
            }

            Uri? uri;
            if (!Uri.TryCreate(ServiceUrl.Trim(), UriKind.Absolute, out uri))
            {
                return true;
            }

            string host = uri.Host.ToLowerInvariant();
            return !(host == "amazonaws.com" || host.EndsWith(".amazonaws.com", StringComparison.Ordinal));
        }

        private void Reset()
        {
            _provider = Provider.None;
            _configError = "";
            _azureAuth = null;
            _accountName = "";
            _container = "";
            _sasContainerUrl = "";
            _s3Bucket = "";
            _s3AccessKeyId = "";
            _s3SecretAccessKey = "";
            _s3Region = DefaultS3Region;
        }

        /// <summary>Runs one public operation: new diagnostics, sync-over-async, every exception converted into the returned text.</summary>
        private string Run(string name, Func<CancellationToken, Task> operation)
        {
            _diag.LogFilePath = _logFilePath;
            _diag.BeginOperation("ObjectStorage." + name);
            LastRequestId = "";
            LastError = "";
            try
            {
                AsyncHelper.RunSync(() => operation(CancellationToken.None));
                return "";
            }
            catch (Exception ex)
            {
                LastError = Fail(ex);
                return LastError;
            }
        }

        private string Fail(Exception exception)
        {
            GraphMailException? known = exception as GraphMailException;
            string text;
            if (known != null)
            {
                text = known.Message;
                if (known.ErrorCode.StartsWith("AADSTS", StringComparison.OrdinalIgnoreCase) || string.Equals(known.ErrorCode, "Token", StringComparison.Ordinal))
                {
                    // Entra mode: token errors get the same AADSTS hints as Graph (wrong secret, missing consent, ...).
                    text = ErrorHints.Append(text, ErrorHints.GetHint(known.ErrorCode, 0, false, known.Message));
                }

                if (known.RequestId.Length > 0)
                {
                    LastRequestId = known.RequestId;
                }
            }
            else if (exception is OperationCanceledException)
            {
                text = "Operation canceled: " + exception.Message;
            }
            else
            {
                text = "Object storage: unexpected error (" + exception.GetType().Name + "): " + GraphClient.FirstLine(exception.Message);
            }

            text = StorageHttp.Redact(text);
            _diag.Log("FAILED: " + text);
            if (known == null)
            {
                _diag.Log(StorageHttp.Redact(exception.ToString()));
            }

            return text;
        }

        /// <summary>Moves the finished temporary file to <paramref name="path"/>, replacing an existing file.</summary>
        private static void ReplaceFile(string temp, string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw GraphMailException.Validation("Download failed: the file cannot be written: " + path + " (" + ex.Message + ")", ex);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}
