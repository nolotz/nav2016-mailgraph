using System;

namespace NavMailGraph
{
    /// <summary>
    /// An OAuth 2.0 access token together with its absolute expiry time.
    /// The token value is never included in <see cref="ToString"/> so it cannot leak through logging by accident.
    /// </summary>
    public sealed class AccessToken
    {
        /// <summary>Creates a token.</summary>
        /// <param name="token">The bearer token value.</param>
        /// <param name="expiresOn">Absolute expiry time (UTC).</param>
        public AccessToken(string token, DateTimeOffset expiresOn)
        {
            Token = token ?? throw new ArgumentNullException(nameof(token));
            ExpiresOn = expiresOn;
        }

        /// <summary>The bearer token value (secret, never log it).</summary>
        public string Token { get; }

        /// <summary>Absolute expiry time of the token.</summary>
        public DateTimeOffset ExpiresOn { get; }

        /// <summary>Returns a description without the token value.</summary>
        public override string ToString()
        {
            return "AccessToken(expires " + ExpiresOn.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}
