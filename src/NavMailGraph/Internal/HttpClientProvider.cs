using System;
using System.Net.Http;
using System.Threading;
#if NET462
using System.Net;
#endif

namespace NavMailGraph.Internal
{
    /// <summary>
    /// One process-wide <see cref="HttpClient"/> (sockets are pooled per instance; creating one per send would
    /// exhaust ports on a busy NAV service tier). The client has no timeout of its own; every request carries a
    /// <see cref="CancellationTokenSource"/> derived from <see cref="GraphMailSender.TimeoutSeconds"/>.
    /// </summary>
    internal static class HttpClientProvider
    {
        private static readonly object Sync = new object();

        private static HttpClient? _client;

        /// <summary>
        /// Test hook: when set before the first use, the static client is built on this handler instead of a
        /// default <see cref="HttpClientHandler"/>. Prefer the internal <see cref="GraphMailSender"/> constructor
        /// that takes an <see cref="HttpClient"/> for per-test isolation.
        /// </summary>
        internal static Func<HttpMessageHandler>? HandlerFactory;

        static HttpClientProvider()
        {
#if NET462
            // .NET Framework 4.6.2 defaults to SSL3|TLS on older Windows versions; Entra and Graph require TLS 1.2.
            // https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (ServicePointManager.DefaultConnectionLimit < 10)
            {
                ServicePointManager.DefaultConnectionLimit = 10;
            }

            // The 100-continue handshake adds a round trip to every POST/PUT and some proxies mishandle it.
            ServicePointManager.Expect100Continue = false;
#endif
        }

        /// <summary>
        /// Runs the static constructor (TLS 1.2 and ServicePointManager defaults on net462) if it has not run yet. Called from
        /// the <see cref="GraphMailSender"/> constructor so the process-wide settings are in place before any request,
        /// including MSAL's token requests (MSAL uses its own HttpClient and never touches <see cref="Client"/>).
        /// The body is intentionally empty: any access to a static member triggers the explicit static constructor.
        /// </summary>
        internal static void EnsureInitialized()
        {
        }

        /// <summary>The shared client (created on first use).</summary>
        internal static HttpClient Client
        {
            get
            {
                HttpClient? client = _client;
                if (client != null)
                {
                    return client;
                }

                lock (Sync)
                {
                    if (_client == null)
                    {
                        Func<HttpMessageHandler>? factory = HandlerFactory;
                        _client = Create(factory != null ? factory() : CreateDefaultHandler());
                    }

                    return _client;
                }
            }
        }

        /// <summary>Creates a client with an infinite timeout on top of <paramref name="handler"/> (used by tests too).</summary>
        internal static HttpClient Create(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler, true);
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        private static HttpMessageHandler CreateDefaultHandler()
        {
            // System proxy (WinHTTP / IE settings on Windows), no credentials, certificate validation untouched.
            return new HttpClientHandler
            {
                UseProxy = true,
                UseCookies = false,
                AllowAutoRedirect = false,
            };
        }
    }
}
