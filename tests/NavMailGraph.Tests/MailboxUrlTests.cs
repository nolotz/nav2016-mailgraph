using NavMailGraph.Graph;
using Xunit;

namespace NavMailGraph.Tests
{
    public class MailboxUrlTests
    {
        [Theory]
        [InlineData("nav@contoso.com", "/nav@contoso.com")]
        [InlineData("first.last@contoso.com", "/first.last@contoso.com")]
        [InlineData("a#b@contoso.com", "/a%23b@contoso.com")]
        [InlineData("a b@contoso.com", "/a%20b@contoso.com")]
        [InlineData("a+b@contoso.com", "/a%2Bb@contoso.com")]
        [InlineData("a/b@contoso.com", "/a%2Fb@contoso.com")]
        [InlineData("1b2c3d4e-0000-1111-2222-333344445555", "/1b2c3d4e-0000-1111-2222-333344445555")]
        [InlineData("$adm@contoso.com", "('$adm@contoso.com')")]
        [InlineData("$o'neil@contoso.com", "('$o''neil@contoso.com')")]
        public void UserSegment_encoding(string mailbox, string expected)
        {
            Assert.Equal(expected, GraphClient.UserSegment(mailbox));
        }

        [Fact]
        public void At_sign_is_never_percent_encoded()
        {
            Assert.DoesNotContain("%40", GraphClient.UserSegment("x@y.de"));
            Assert.DoesNotContain("%40", GraphClient.EscapePathSegment("x@y.de"));
        }

        [Theory]
        [InlineData("AAMkAGI2THVSAAA=", "AAMkAGI2THVSAAA=")]
        [InlineData("AAMk+x/y==", "AAMk%2Bx%2Fy==")]
        [InlineData("", "")]
        public void EscapePathSegment_keeps_equals_literal(string value, string expected)
        {
            Assert.Equal(expected, GraphClient.EscapePathSegment(value));
        }
    }
}
