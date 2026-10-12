using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NavMailGraph.Auth
{
    /// <summary>Loads the app certificate from the Windows certificate stores or from a PFX file.</summary>
    internal static class CertificateLoader
    {
        /// <summary>
        /// Strips whitespace and every non-hex character (certmgr copies a hidden left-to-right mark U+200E in front of
        /// thumbprints) and upper-cases the rest.
        /// </summary>
        internal static string NormalizeThumbprint(string thumbprint)
        {
            if (string.IsNullOrEmpty(thumbprint))
            {
                return "";
            }

            var sb = new StringBuilder(thumbprint.Length);
            foreach (char c in thumbprint)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                {
                    sb.Append(char.ToUpperInvariant(c));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Finds the certificate by thumbprint in LocalMachine\My, then CurrentUser\My (validOnly = false so expired or
        /// self-signed certificates are found; Entra validates the registered key, not the chain). Requires a private key.
        /// </summary>
        internal static X509Certificate2 LoadFromStore(string thumbprint)
        {
            string normalized = NormalizeThumbprint(thumbprint);
            if (normalized.Length == 0)
            {
                throw GraphMailException.Validation("Validation failed: certificate thumbprint is empty.");
            }

            var storeErrors = new List<string>();
            string? foundWithoutKey = null;
            foreach (StoreLocation location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                X509Store? store = null;
                try
                {
                    // On macOS/Linux the LocalMachine store may throw; on Windows a locked-down service account may lack access.
                    store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, false);
                    foreach (X509Certificate2 candidate in found)
                    {
                        if (candidate.HasPrivateKey)
                        {
                            return candidate;
                        }

                        foundWithoutKey = location + "\\My";
                    }
                }
                catch (Exception ex)
                {
                    storeErrors.Add(location + "\\My: " + ex.Message);
                }
                finally
                {
                    if (store != null)
                    {
                        store.Close();
                    }
                }
            }

            if (foundWithoutKey != null)
            {
                throw GraphMailException.Validation(
                    "Certificate " + normalized + " was found in " + foundWithoutKey + " but has no private key. "
                    + "Import the PFX including the private key and grant the NAV service account read access to it.");
            }

            string text = "Certificate with thumbprint " + normalized + " not found in LocalMachine\\My or CurrentUser\\My.";
            if (storeErrors.Count > 0)
            {
                text += " (" + string.Join("; ", storeErrors.ToArray()) + ")";
            }

            throw GraphMailException.Validation(text);
        }

        /// <summary>
        /// Loads a PFX/PKCS#12 file. MachineKeySet is tried first: the NAV service account usually has no loadable user
        /// profile, so the user key store fails with "The system cannot find the file specified". Platforms that reject
        /// MachineKeySet (macOS) fall back to the default key set.
        /// </summary>
        internal static X509Certificate2 LoadFromFile(string pfxPath, string pfxPassword)
        {
            if (string.IsNullOrWhiteSpace(pfxPath))
            {
                throw GraphMailException.Validation("Validation failed: certificate file path is empty.");
            }

            if (!File.Exists(pfxPath))
            {
                throw GraphMailException.Validation("Certificate file not found: " + pfxPath);
            }

            X509Certificate2 certificate;
            try
            {
                certificate = new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.MachineKeySet);
            }
            catch (Exception)
            {
                try
                {
                    certificate = new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.DefaultKeySet);
                }
                catch (Exception ex)
                {
                    // ex.Message never contains the password ("The specified network password is not correct." etc.).
                    throw GraphMailException.Validation("Certificate file '" + pfxPath + "' could not be loaded: " + ex.Message, ex);
                }
            }

            if (!certificate.HasPrivateKey)
            {
                throw GraphMailException.Validation("Certificate file '" + pfxPath + "' does not contain a private key.");
            }

            return certificate;
        }

        /// <summary>Returns a warning text when the certificate is outside its validity period, else null. Entra rejects expired certificates.</summary>
        internal static string? ValidityWarning(X509Certificate2 certificate, DateTime utcNow)
        {
            DateTime notAfter = certificate.NotAfter.ToUniversalTime();
            DateTime notBefore = certificate.NotBefore.ToUniversalTime();
            if (notAfter < utcNow)
            {
                return "certificate " + certificate.Thumbprint + " expired on " + notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (UTC)";
            }

            if (notBefore > utcNow)
            {
                return "certificate " + certificate.Thumbprint + " is not valid before " + notBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (UTC)";
            }

            return null;
        }
    }
}
