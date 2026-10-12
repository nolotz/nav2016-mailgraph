using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph
{
    /// <summary>
    /// Supplies access tokens for Microsoft Graph. Implemented by the built-in client-credentials provider,
    /// by the optional MSAL assembly and by tests/CLI tools.
    /// </summary>
    public interface ITokenProvider
    {
        /// <summary>
        /// Returns a valid access token, acquiring a new one when none is cached, when the cached one is about to
        /// expire or when <paramref name="forceRefresh"/> is set.
        /// </summary>
        /// <param name="forceRefresh">Bypass any cache and acquire a fresh token (used once after an HTTP 401).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);

        /// <summary>One-line description for diagnostics, e.g. "BuiltIn/ClientSecret tenant=... client=...". Must not contain secrets.</summary>
        string Describe();
    }
}
