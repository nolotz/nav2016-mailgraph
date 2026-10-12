using System;
using NavMailGraph.Internal;
using NavMailGraph.Tests.Infrastructure;
using Xunit;

namespace NavMailGraph.Tests
{
    public class MsalBridgeTests
    {
        private const string Secret = "msal-Secret~Value-987";

        [Fact]
        public void TokenLibrary_Msal_resolves_MsalTokenProvider_through_the_bridge_without_network()
        {
            Type? type = Type.GetType(MsalBridge.TypeName + ", " + MsalBridge.AssemblyName, false);
            Assert.True(type != null, "NavMailGraph.Msal.MsalTokenProvider is missing from NavMailGraph.Msal.dll (the MSAL project is still a placeholder?).");

            var handler = new FakeHttpMessageHandler();
            GraphMailSender sender = GraphMailSender.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, handler);
            sender.UseClientSecret(Secret);
            sender.TokenLibrary = "msal";

            ITokenProvider provider = sender.ResolveTokenProvider(sender.ResolveEndpoints());
            Assert.Equal(MsalBridge.TypeName, provider.GetType().FullName);
            string description = provider.Describe();
            Assert.False(string.IsNullOrEmpty(description));
            Assert.DoesNotContain(Secret, description);
            Assert.Same(provider, sender.ResolveTokenProvider(sender.ResolveEndpoints()));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void Bridge_requires_settings()
        {
            Assert.Throws<ArgumentNullException>(() => MsalBridge.Create(null!));
        }

        [Fact]
        public void Switching_back_to_builtin_resets_provider()
        {
            GraphMailSender sender = GraphMailSender.CreateForTests(TestSupport.Tenant, TestSupport.ClientId, new FakeHttpMessageHandler());
            sender.UseClientSecret(Secret);
            sender.TokenLibrary = "BuiltIn";
            ITokenProvider provider = sender.ResolveTokenProvider(sender.ResolveEndpoints());
            Assert.Equal("NavMailGraph.Auth.BuiltInTokenProvider", provider.GetType().FullName);
            Assert.DoesNotContain(Secret, provider.Describe());
        }
    }
}
