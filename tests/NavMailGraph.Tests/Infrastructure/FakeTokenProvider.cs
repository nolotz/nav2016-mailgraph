using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NavMailGraph.Tests.Infrastructure
{
    /// <summary>Token provider for tests: hands out "fake-token-1", "fake-token-2", ... and records every call.</summary>
    internal sealed class FakeTokenProvider : ITokenProvider
    {
        private readonly object _sync = new object();

        private readonly List<bool> _calls = new List<bool>();

        private int _issued;

        private AccessToken? _current;

        /// <summary>When set, GetAccessTokenAsync throws this exception.</summary>
        internal Exception? ThrowOnAcquire { get; set; }

        /// <summary>forceRefresh flag of every call, in order.</summary>
        internal IReadOnlyList<bool> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        internal int Issued
        {
            get
            {
                lock (_sync)
                {
                    return _issued;
                }
            }
        }

        public Task<AccessToken> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _calls.Add(forceRefresh);
                if (ThrowOnAcquire != null)
                {
                    throw ThrowOnAcquire;
                }

                if (_current == null || forceRefresh)
                {
                    _issued++;
                    _current = new AccessToken("fake-token-" + _issued.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddHours(1));
                }

                return Task.FromResult(_current);
            }
        }

        public string Describe()
        {
            return "FakeTokenProvider";
        }
    }
}
