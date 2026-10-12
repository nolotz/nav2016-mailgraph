using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NavMailGraph.Auth
{
    /// <summary>
    /// Builds the signed JWT ("client assertion") that proves possession of the app certificate.
    /// Format per https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials :
    /// header {"alg":"RS256","typ":"JWT","x5t":base64url(SHA-1 thumbprint)} (+ "x5c" when requested),
    /// payload {aud, iss, sub, jti, nbf, exp, iat}, signature RSASSA-PKCS1-v1_5 with SHA-256.
    /// </summary>
    internal static class ClientAssertionBuilder
    {
        /// <summary>Seconds the assertion is valid before "now" (clock skew tolerance).</summary>
        internal const int NotBeforeSkewSeconds = 60;

        /// <summary>Assertion lifetime in seconds (Entra accepts at most 10 minutes).</summary>
        internal const int LifetimeSeconds = 600;

        internal static string Build(X509Certificate2 certificate, string clientId, string audience, bool sendX5C, DateTimeOffset now)
        {
            if (certificate == null)
            {
                throw new ArgumentNullException(nameof(certificate));
            }

            var header = new StringBuilder();
            header.Append("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"x5t\":").Append(JsonString(Base64UrlEncode(certificate.GetCertHash())));
            if (sendX5C)
            {
                // x5c lets Entra validate against the subject name + issuer (certificate auto-rotation / SNI) instead of a
                // registered thumbprint. RawData is the DER-encoded certificate; x5c wants standard base64.
                header.Append(",\"x5c\":[").Append(JsonString(Convert.ToBase64String(certificate.RawData))).Append(']');
            }

            header.Append('}');

            long nowSeconds = now.ToUnixTimeSeconds();
            var payload = new StringBuilder();
            payload.Append("{\"aud\":").Append(JsonString(audience));
            payload.Append(",\"iss\":").Append(JsonString(clientId));
            payload.Append(",\"sub\":").Append(JsonString(clientId));
            payload.Append(",\"jti\":").Append(JsonString(Guid.NewGuid().ToString("D")));
            payload.Append(",\"nbf\":").Append((nowSeconds - NotBeforeSkewSeconds).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"exp\":").Append((nowSeconds + LifetimeSeconds).ToString(CultureInfo.InvariantCulture));
            payload.Append(",\"iat\":").Append(nowSeconds.ToString(CultureInfo.InvariantCulture));
            payload.Append('}');

            string signingInput = Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToString())) + "." + Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToString()));
            byte[] signature = RsaSigner.SignSha256Pkcs1(certificate, Encoding.UTF8.GetBytes(signingInput));
            return signingInput + "." + Base64UrlEncode(signature);
        }

        /// <summary>Base64url without padding (RFC 7515 section 2).</summary>
        internal static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Quotes and escapes a JSON string value.</summary>
        internal static string JsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// RSA-SHA256 signing with the certificate's private key, including the well-known workaround for legacy
    /// CryptoAPI keys: certificates imported with "Microsoft Base/Strong Cryptographic Provider" (provider type 1)
    /// cannot sign with SHA-256 and throw "Invalid algorithm specified". The same key container can be re-opened with
    /// the "Microsoft Enhanced RSA and AES Cryptographic Provider" (provider type 24), which supports SHA-256.
    /// </summary>
    internal static class RsaSigner
    {
        internal const int EnhancedRsaAesProviderType = 24;

        internal const string EnhancedRsaAesProviderName = "Microsoft Enhanced RSA and AES Cryptographic Provider";

        internal static byte[] SignSha256Pkcs1(X509Certificate2 certificate, byte[] data)
        {
            RSA? rsa;
            try
            {
                rsa = certificate.GetRSAPrivateKey();
            }
            catch (CryptographicException ex)
            {
                throw GraphMailException.Validation(
                    "Certificate " + certificate.Thumbprint + ": the private key could not be opened (" + ex.Message
                    + "). Grant the NAV service account read access to the private key.", ex);
            }

            if (rsa == null)
            {
                throw GraphMailException.Validation("Certificate " + certificate.Thumbprint + " has no RSA private key (ECDSA certificates are not supported by this library).");
            }

            try
            {
                return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException ex) when (rsa is RSACryptoServiceProvider)
            {
                return SignWithEnhancedProvider((RSACryptoServiceProvider)rsa, data, ex);
            }
            finally
            {
                // On .NET Framework GetRSAPrivateKey() returns the certificate's cached RSACryptoServiceProvider for
                // CryptoAPI keys (X509Certificate2.PrivateKey); disposing it would break the next Send. CNG/OpenSSL
                // wrappers are fresh objects and must be disposed to release their key handles.
                if (!(rsa is RSACryptoServiceProvider))
                {
                    rsa.Dispose();
                }
            }
        }

        private static byte[] SignWithEnhancedProvider(RSACryptoServiceProvider original, byte[] data, CryptographicException originalError)
        {
            RSACryptoServiceProvider? reopened = null;
            try
            {
                CspKeyContainerInfo info = original.CspKeyContainerInfo;
                var parameters = new CspParameters(EnhancedRsaAesProviderType, EnhancedRsaAesProviderName, info.KeyContainerName)
                {
                    KeyNumber = (int)info.KeyNumber,
                    Flags = CspProviderFlags.UseExistingKey,
                };
                if (info.MachineKeyStore)
                {
                    parameters.Flags |= CspProviderFlags.UseMachineKeyStore;
                }

                reopened = new RSACryptoServiceProvider(parameters);
                // Never delete the key container when this wrapper is disposed.
                reopened.PersistKeyInCsp = true;
                return reopened.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException ex)
            {
                throw GraphMailException.Validation(
                    "Certificate private key cannot sign with SHA-256 (" + originalError.Message + "; re-opening the key with provider type "
                    + EnhancedRsaAesProviderType + " failed: " + ex.Message + "). Re-import the certificate as a CNG key or with the '"
                    + EnhancedRsaAesProviderName + "'.", ex);
            }
            finally
            {
                if (reopened != null)
                {
                    reopened.Dispose();
                }
            }
        }
    }
}
