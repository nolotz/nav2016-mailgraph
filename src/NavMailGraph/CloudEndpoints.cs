using System;

namespace NavMailGraph
{
    /// <summary>
    /// Authority host, Graph base URL and default scope of a Microsoft cloud.
    /// Source: https://learn.microsoft.com/en-us/graph/deployments (national cloud deployments), see
    /// docs/research-notes.md and docs/DESIGN.md section 11.
    /// </summary>
    internal sealed class CloudEndpoints
    {
        internal const string GlobalName = "Global";
        internal const string UsGovName = "USGov";
        internal const string UsGovDoDName = "USGovDoD";
        internal const string ChinaName = "China";

        internal static readonly CloudEndpoints Global = new CloudEndpoints(
            GlobalName,
            "https://login.microsoftonline.com",
            "https://graph.microsoft.com/v1.0",
            "https://graph.microsoft.com/.default",
            true);

        /// <summary>US Government L4 (GCC High). createUploadSession is not available there.</summary>
        internal static readonly CloudEndpoints UsGov = new CloudEndpoints(
            UsGovName,
            "https://login.microsoftonline.us",
            "https://graph.microsoft.us/v1.0",
            "https://graph.microsoft.us/.default",
            false);

        /// <summary>US Government L5 (DoD). createUploadSession is not available there.</summary>
        internal static readonly CloudEndpoints UsGovDoD = new CloudEndpoints(
            UsGovDoDName,
            "https://login.microsoftonline.us",
            "https://dod-graph.microsoft.us/v1.0",
            "https://dod-graph.microsoft.us/.default",
            false);

        /// <summary>Microsoft Graph China operated by 21Vianet.</summary>
        internal static readonly CloudEndpoints China = new CloudEndpoints(
            ChinaName,
            "https://login.chinacloudapi.cn",
            "https://microsoftgraph.chinacloudapi.cn/v1.0",
            "https://microsoftgraph.chinacloudapi.cn/.default",
            true);

        private CloudEndpoints(string name, string authorityHost, string graphBaseUrl, string scope, bool supportsUploadSessions)
        {
            Name = name;
            AuthorityHost = authorityHost;
            GraphBaseUrl = graphBaseUrl;
            Scope = scope;
            SupportsUploadSessions = supportsUploadSessions;
        }

        internal string Name { get; }

        /// <summary>Authority host without tenant and without trailing slash.</summary>
        internal string AuthorityHost { get; }

        /// <summary>Graph base URL including the API version, without trailing slash.</summary>
        internal string GraphBaseUrl { get; }

        /// <summary>Default client-credentials scope ("{graph resource}/.default").</summary>
        internal string Scope { get; }

        /// <summary>
        /// False for the US Government L4/L5 clouds: attachment createUploadSession is documented as not available there
        /// (https://learn.microsoft.com/en-us/graph/api/attachment-createuploadsession, availability table).
        /// </summary>
        internal bool SupportsUploadSessions { get; }

        /// <summary>Resolves a cloud name (case-insensitive; "" means Global). Throws a validation error for unknown names.</summary>
        internal static CloudEndpoints Resolve(string cloud)
        {
            string name = (cloud ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, GlobalName, StringComparison.OrdinalIgnoreCase))
            {
                return Global;
            }

            if (string.Equals(name, UsGovName, StringComparison.OrdinalIgnoreCase))
            {
                return UsGov;
            }

            if (string.Equals(name, UsGovDoDName, StringComparison.OrdinalIgnoreCase))
            {
                return UsGovDoD;
            }

            if (string.Equals(name, ChinaName, StringComparison.OrdinalIgnoreCase))
            {
                return China;
            }

            throw GraphMailException.Validation("Validation failed: unknown Cloud '" + name + "' (expected Global, USGov, USGovDoD or China).");
        }

        /// <summary>
        /// True when <paramref name="graphBaseUrl"/> points to a US Government Graph host (graph.microsoft.us or
        /// dod-graph.microsoft.us), so an explicit GraphBaseUrl override is subject to the same upload-session restriction.
        /// </summary>
        internal static bool IsUsGovernmentGraphHost(string graphBaseUrl)
        {
            Uri? uri;
            if (!Uri.TryCreate(graphBaseUrl, UriKind.Absolute, out uri) || uri == null)
            {
                return false;
            }

            string host = uri.Host;
            return string.Equals(host, "graph.microsoft.us", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "dod-graph.microsoft.us", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Derives the ".default" scope from a Graph base URL, e.g. "https://graph.microsoft.us/v1.0" -> "https://graph.microsoft.us/.default".
        /// Used when GraphBaseUrl is overridden explicitly so the token is requested for the right resource.
        /// </summary>
        internal static string ScopeFromGraphBaseUrl(string graphBaseUrl)
        {
            Uri? uri;
            if (!Uri.TryCreate(graphBaseUrl, UriKind.Absolute, out uri) || uri == null
                || !(string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
            {
                throw GraphMailException.Validation("Validation failed: GraphBaseUrl '" + graphBaseUrl + "' is not an absolute http(s) URL.");
            }

            return uri.GetLeftPart(UriPartial.Authority) + "/.default";
        }
    }
}
