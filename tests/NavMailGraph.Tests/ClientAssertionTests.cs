#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using NavMailGraph.Auth;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    // Self-signed certificates via CertificateRequest: the API exists on .NET Framework 4.7.2+, not 4.6.2, so these
    // tests only run on the modern runtime.
    public class ClientAssertionTests
    {
        private const string Audience = "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token";

        private static X509Certificate2 CreateCertificate()
        {
            return CreateCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        }

        /// <summary>
        /// Self-signed RSA certificate via CertificateRequest. CreateSelfSigned/CopyWithPrivateKey need a writable default
        /// keychain on macOS, so the public certificate is signed with an explicit generator and bundled with the key into
        /// a PFX (Pkcs12Builder), which every platform can load.
        /// </summary>
        private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                var subject = new X500DistinguishedName("CN=NavMailGraph Test");
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                byte[] serial = Guid.NewGuid().ToByteArray();
                serial[0] &= 0x7F;
                using (X509Certificate2 publicOnly = request.Create(subject, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1), notBefore, notAfter, serial))
                {
                    var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048);
                    var contents = new Pkcs12SafeContents();
                    contents.AddCertificate(publicOnly);
                    contents.AddShroudedKey(rsa, "tmp", pbe);
                    var builder = new Pkcs12Builder();
                    builder.AddSafeContentsUnencrypted(contents);
                    builder.SealWithMac("tmp", HashAlgorithmName.SHA256, 2048);
                    return new X509Certificate2(builder.Encode(), "tmp", X509KeyStorageFlags.Exportable);
                }
            }
        }

        private sealed class Jwt
        {
            internal JsonElement Header;
            internal JsonElement Payload;
            internal string SigningInput = "";
            internal byte[] Signature = Array.Empty<byte>();
        }

        private static Jwt Decode(string assertion)
        {
            string[] parts = assertion.Split('.');
            Assert.Equal(3, parts.Length);
            return new Jwt
            {
                Header = TestSupport.ParseJson(Encoding.UTF8.GetString(TestSupport.Base64UrlDecode(parts[0]))),
                Payload = TestSupport.ParseJson(Encoding.UTF8.GetString(TestSupport.Base64UrlDecode(parts[1]))),
                SigningInput = parts[0] + "." + parts[1],
                Signature = TestSupport.Base64UrlDecode(parts[2]),
            };
        }

        [Fact]
        public void Assertion_header_payload_and_signature()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            {
                var now = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
                string assertion = ClientAssertionBuilder.Build(certificate, TestSupport.ClientId, Audience, false, now);
                Assert.DoesNotContain("=", assertion);
                Jwt jwt = Decode(assertion);

                Assert.Equal("RS256", jwt.Header.GetProperty("alg").GetString());
                Assert.Equal("JWT", jwt.Header.GetProperty("typ").GetString());
                Assert.Equal(TestSupport.Base64UrlEncode(SHA1.HashData(certificate.RawData)), jwt.Header.GetProperty("x5t").GetString());
                Assert.False(TestSupport.Has(jwt.Header, "x5c"));

                long iat = now.ToUnixTimeSeconds();
                Assert.Equal(Audience, jwt.Payload.GetProperty("aud").GetString());
                Assert.Equal(TestSupport.ClientId, jwt.Payload.GetProperty("iss").GetString());
                Assert.Equal(TestSupport.ClientId, jwt.Payload.GetProperty("sub").GetString());
                Assert.Equal(iat, jwt.Payload.GetProperty("iat").GetInt64());
                Assert.Equal(iat - 60, jwt.Payload.GetProperty("nbf").GetInt64());
                Assert.Equal(iat + 600, jwt.Payload.GetProperty("exp").GetInt64());
                Assert.True(Guid.TryParse(jwt.Payload.GetProperty("jti").GetString(), out _));

                using (RSA publicKey = certificate.GetRSAPublicKey()!)
                {
                    Assert.True(publicKey.VerifyData(Encoding.UTF8.GetBytes(jwt.SigningInput), jwt.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                }
            }
        }

        [Fact]
        public void Assertion_with_x5c()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            {
                Jwt jwt = Decode(ClientAssertionBuilder.Build(certificate, "c", Audience, true, DateTimeOffset.UtcNow));
                JsonElement x5c = jwt.Header.GetProperty("x5c");
                Assert.Equal(Convert.ToBase64String(certificate.RawData), Assert.Single(x5c.EnumerateArray()).GetString());
            }
        }

        [Fact]
        public void Each_assertion_has_a_new_jti()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            {
                var now = DateTimeOffset.UtcNow;
                string a = Decode(ClientAssertionBuilder.Build(certificate, "c", Audience, false, now)).Payload.GetProperty("jti").GetString()!;
                string b = Decode(ClientAssertionBuilder.Build(certificate, "c", Audience, false, now)).Payload.GetProperty("jti").GetString()!;
                Assert.NotEqual(a, b);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Certificate_file_end_to_end_posts_client_assertion(bool sendX5C)
        {
            GraphMailSender.ResetTokenCacheForTests();
            using (X509Certificate2 certificate = CreateCertificate())
            using (var dir = new TempDirectory())
            {
                string pfx = dir.File("app.pfx", certificate.Export(X509ContentType.Pfx, "pfx-pw"));
                string tenant = Guid.NewGuid().ToString("D");
                var handler = new FakeHttpMessageHandler()
                    .Enqueue(200, "{\"token_type\":\"Bearer\",\"expires_in\":3599,\"access_token\":\"at-cert\"}")
                    .Enqueue(202);
                GraphMailSender sender = GraphMailSender.CreateForTests(tenant, TestSupport.ClientId, handler);
                sender.UseCertificateFile(pfx, "pfx-pw");
                sender.SendX5C = sendX5C;
                Assert.Equal("", sender.Send(TestSupport.NewMessage()));

                Dictionary<string, string> form = TestSupport.Form(handler.Requests[0]);
                Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
                Assert.False(form.ContainsKey("client_secret"));
                Assert.Equal("client_credentials", form["grant_type"]);
                Jwt jwt = Decode(form["client_assertion"]);
                Assert.Equal("https://login.microsoftonline.com/" + tenant + "/oauth2/v2.0/token", jwt.Payload.GetProperty("aud").GetString());
                Assert.Equal(sendX5C, TestSupport.Has(jwt.Header, "x5c"));
                Assert.Equal("Bearer at-cert", handler.Requests[1].Authorization);
                Assert.DoesNotContain("pfx-pw", sender.LastDiagnostics);
                Assert.DoesNotContain(form["client_assertion"], sender.LastDiagnostics);
                Assert.Contains("BuiltIn/Certificate", sender.LastDiagnostics);
            }
        }

        [Fact]
        public void Wrong_pfx_password_is_a_clear_error()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            using (var dir = new TempDirectory())
            {
                string pfx = dir.File("app.pfx", certificate.Export(X509ContentType.Pfx, "right"));
                var handler = new FakeHttpMessageHandler();
                GraphMailSender sender = GraphMailSender.CreateForTests("t", "c", handler);
                sender.UseCertificateFile(pfx, "wrong-password-xyz");
                string result = sender.TestConnection();
                Assert.StartsWith("Certificate file '" + pfx + "' could not be loaded: ", result);
                Assert.DoesNotContain("wrong-password-xyz", result);
                Assert.Empty(handler.Requests);
            }
        }

        [Fact]
        public void Certificate_without_private_key_in_file_is_rejected()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            using (var dir = new TempDirectory())
            {
                string cer = dir.File("app.cer", certificate.Export(X509ContentType.Cert));
                GraphMailSender sender = GraphMailSender.CreateForTests("t", "c", new FakeHttpMessageHandler());
                sender.UseCertificateFile(cer, "");
                Assert.Contains("does not contain a private key", sender.TestConnection());
            }
        }

        [Theory]
        [InlineData("‎ab cd:EF-01", "ABCDEF01")]
        [InlineData("", "")]
        public void Thumbprint_normalization(string input, string expected)
        {
            Assert.Equal(expected, CertificateLoader.NormalizeThumbprint(input));
        }

        [Fact]
        public void Validity_warning()
        {
            using (X509Certificate2 certificate = CreateCertificate())
            {
                Assert.Null(CertificateLoader.ValidityWarning(certificate, DateTime.UtcNow));
                Assert.Contains("expired", CertificateLoader.ValidityWarning(certificate, DateTime.UtcNow.AddDays(60)));
                Assert.Contains("not valid before", CertificateLoader.ValidityWarning(certificate, DateTime.UtcNow.AddDays(-5)));
            }
        }
    }
}
#endif
