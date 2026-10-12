using System.Security.Cryptography.X509Certificates;

namespace NavMailGraph
{
    /// <summary>
    /// Everything a client-credentials token provider needs. <see cref="GraphMailSender"/> fills it from its own
    /// settings and hands it to the built-in provider or (by reflection) to
    /// <c>NavMailGraph.Msal.MsalTokenProvider(TokenProviderSettings)</c>. Not intended for C/AL.
    /// </summary>
    public sealed class TokenProviderSettings
    {
        /// <summary>Creates empty settings.</summary>
        public TokenProviderSettings()
        {
        }

        /// <summary>Entra tenant id (GUID) or verified domain (contoso.onmicrosoft.com).</summary>
        public string TenantId { get; set; } = "";

        /// <summary>Application (client) id of the app registration.</summary>
        public string ClientId { get; set; } = "";

        /// <summary>Authority host without tenant, e.g. "https://login.microsoftonline.com".</summary>
        public string AuthorityHost { get; set; } = "";

        /// <summary>Scope for the client-credentials flow, e.g. "https://graph.microsoft.com/.default".</summary>
        public string Scope { get; set; } = "";

        /// <summary>Client secret; "" when a certificate is used. Secret: never log it.</summary>
        public string ClientSecret { get; set; } = "";

        /// <summary>Certificate with private key for the JWT client assertion; null when a secret is used.</summary>
        public X509Certificate2? Certificate { get; set; }

        /// <summary>Include the x5c (public certificate) claim in the client assertion (Entra certificate auto-rotation / SNI).</summary>
        public bool SendX5C { get; set; }

        /// <summary>True when a client secret is configured.</summary>
        public bool HasClientSecret
        {
            get { return !string.IsNullOrEmpty(ClientSecret); }
        }

        /// <summary>True when a certificate is configured.</summary>
        public bool HasCertificate
        {
            get { return Certificate != null; }
        }

        /// <summary>Token endpoint of the v2.0 protocol: {AuthorityHost}/{TenantId}/oauth2/v2.0/token.</summary>
        internal string TokenEndpoint
        {
            get
            {
                // https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-client-creds-grant-flow
                return AuthorityHost.TrimEnd('/') + "/" + System.Uri.EscapeDataString(TenantId.Trim()) + "/oauth2/v2.0/token";
            }
        }
    }
}
