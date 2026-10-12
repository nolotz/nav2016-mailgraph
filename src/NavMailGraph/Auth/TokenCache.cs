using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Auth
{
    /// <summary>
    /// Process-wide token cache keyed by authority|tenant|client|credential fingerprint|scope. A token is reused while
    /// more than <see cref="RefreshSkew"/> of its lifetime is left. Concurrent callers for the same key wait on one
    /// <see cref="SemaphoreSlim"/> so a key is refreshed once, not once per NAV session.
    /// </summary>
    internal static class TokenCache
    {
        /// <summary>Tokens with less remaining lifetime than this are refreshed proactively.</summary>
        internal static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

        private static readonly ConcurrentDictionary<string, AccessToken> Tokens = new ConcurrentDictionary<string, AccessToken>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        internal static int Count
        {
            get { return Tokens.Count; }
        }

        internal static bool IsFresh(AccessToken token)
        {
            return token.ExpiresOn - DateTimeOffset.UtcNow > RefreshSkew;
        }

        /// <summary>Returns the cached token for <paramref name="key"/> or acquires one with <paramref name="acquire"/>.</summary>
        internal static async Task<AccessToken> GetOrAcquireAsync(string key, bool forceRefresh, Func<CancellationToken, Task<AccessToken>> acquire, CancellationToken cancellationToken)
        {
            AccessToken? seen;
            Tokens.TryGetValue(key, out seen);
            if (!forceRefresh && seen != null && IsFresh(seen))
            {
                return seen;
            }

            SemaphoreSlim gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AccessToken? current;
                if (Tokens.TryGetValue(key, out current) && current != null && IsFresh(current))
                {
                    // Another caller refreshed while we were waiting; a forced refresh is satisfied by any token that
                    // is newer than the one we saw before waiting.
                    if (!forceRefresh || !ReferenceEquals(current, seen))
                    {
                        return current;
                    }
                }

                AccessToken fresh = await acquire(cancellationToken).ConfigureAwait(false);
                Tokens[key] = fresh;
                return fresh;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Removes all cached tokens (tests, or after a credential rotation).</summary>
        internal static void Clear()
        {
            Tokens.Clear();
        }

        /// <summary>Removes the token for one key.</summary>
        internal static void Remove(string key)
        {
            AccessToken? ignored;
            Tokens.TryRemove(key, out ignored);
        }
    }
}
