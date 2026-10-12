using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using NavMailGraph.Graph;

namespace NavMailGraph.Storage
{
    /// <summary>
    /// Entra ID (OAuth 2.0 client credentials) authorization for Azure Blob Storage: "Authorization: Bearer &lt;token&gt;" with a
    /// token for the resource https://storage.azure.com/ (scope https://storage.azure.com/.default) and x-ms-version 2017-11-09
    /// or later (docs/research-notes-v2.md, "authorize-with-azure-active-directory"). The token provider comes from a
    /// <see cref="GraphConnection"/> (same app registration and credential as Graph); it is created lazily on the first request,
    /// so configuration problems surface as the error text of the first operation. Tokens are cached process-wide per scope.
    /// </summary>
    internal sealed class AzureEntraAuth : IStorageAuth
    {
        /// <summary>Token scope for Azure Storage (resource id https://storage.azure.com/ + "/.default").</summary>
        internal const string StorageScope = "https://storage.azure.com/.default";

        /// <summary>Oldest x-ms-version that accepts Entra bearer tokens.</summary>
        internal const string MinimumApiVersion = "2017-11-09";

        internal const string DownloadUrlNotSupported =
            "GetDownloadUrl is not available in Entra ID mode (UseAzureBlobEntra): download links need a shared key (UseAzureBlobSharedKey) or a SAS URL "
            + "(UseAzureBlobSasUrl). User delegation SAS links are not supported yet.";

        private readonly Func<ITokenProvider> _factory;

        private readonly string _description;

        private readonly object _sync = new object();

        private ITokenProvider? _provider;

        internal AzureEntraAuth(Func<ITokenProvider> providerFactory, string description)
        {
            _factory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _description = description ?? "";
        }

        public string Mode
        {
            get { return "Entra"; }
        }

        public string AuthQuery
        {
            get { return ""; }
        }

        public bool CanRefresh
        {
            get { return true; }
        }

        public async Task ApplyAsync(HttpRequestMessage request, AzureRequestInfo info, bool forceRefresh, CancellationToken cancellationToken)
        {
            string version = GraphClient.HeaderValue(request.Headers, "x-ms-version");
            if (version.Length > 0 && string.CompareOrdinal(version, MinimumApiVersion) < 0)
            {
                throw GraphMailException.Validation("Azure Blob configuration invalid: Entra ID tokens need AzureApiVersion " + MinimumApiVersion + " or later (got " + version + ").");
            }

            AccessToken token = await GraphClient.AcquireTokenAsync(Provider(), forceRefresh, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        public string CreateReadUrl(string blobUrlWithoutQuery, string accountName, string containerName, string blobName, DateTime expiresUtc, string apiVersion, bool httpsOnly, out string error)
        {
            error = DownloadUrlNotSupported;
            return "";
        }

        public string Describe()
        {
            return "Entra" + (_description.Length > 0 ? " " + _description : "") + " scope=" + StorageScope;
        }

        private ITokenProvider Provider()
        {
            lock (_sync)
            {
                if (_provider == null)
                {
                    _provider = _factory();
                }

                return _provider;
            }
        }
    }
}
