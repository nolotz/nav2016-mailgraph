using System.Linq;
using NavMailGraph.Internal;
using Xunit;

namespace NavMailGraph.Tests
{
    public class ErrorHintsTests
    {
        // Every code listed in docs/DESIGN.md section 11, plus ErrorAttachmentSizeShouldNotBeLessThanMinimumSize (research-notes).
        [Theory]
        [InlineData("ErrorAccessDenied")]
        [InlineData("ErrorSendAsDenied")]
        [InlineData("MailboxNotEnabledForRESTAPI")]
        [InlineData("ResourceNotFound")]
        [InlineData("ErrorAttachmentSizeShouldNotBeLessThanMinimumSize")]
        [InlineData("AADSTS7000215")]
        [InlineData("AADSTS7000222")]
        [InlineData("AADSTS700016")]
        [InlineData("AADSTS90002")]
        [InlineData("AADSTS700027")]
        [InlineData("AADSTS50059")]
        [InlineData("AADSTS9001023")]
        public void Code_table_covers_documented_codes(string code)
        {
            Assert.Contains(ErrorHints.CodeTable, e => e.Key == code && e.Value.Length > 0);
            Assert.NotEqual("", ErrorHints.GetHint(code, 0, false, ""));
            Assert.Equal(ErrorHints.GetHint(code, 0, false, ""), ErrorHints.GetHint(code.ToLowerInvariant(), 0, false, ""));
        }

        [Fact]
        public void Code_table_has_no_duplicates()
        {
            Assert.Equal(ErrorHints.CodeTable.Length, ErrorHints.CodeTable.Select(e => e.Key.ToLowerInvariant()).Distinct().Count());
        }

        [Theory]
        [InlineData(403, "AccessDenied")]
        [InlineData(404, "MailboxNotFound")]
        [InlineData(413, "PayloadTooLarge")]
        public void Status_table(int status, string which)
        {
            string expected = which == "AccessDenied" ? ErrorHints.AccessDeniedHint : which == "MailboxNotFound" ? ErrorHints.MailboxNotFoundHint : ErrorHints.PayloadTooLargeHint;
            Assert.Equal(expected, ErrorHints.GetHint("SomethingElse", status, false, "x"));
            Assert.Equal(expected, ErrorHints.GetHint("HTTP" + status, status, false, null));
        }

        [Fact]
        public void Precedence_rules()
        {
            Assert.Equal(ErrorHints.MailReadWriteHint, ErrorHints.GetHint("ErrorAccessDenied", 403, true, ""));
            Assert.Equal(ErrorHints.SendAsDeniedHint, ErrorHints.GetHint("ErrorSendAsDenied", 403, true, ""));
            Assert.Equal(ErrorHints.AccessDeniedHint, ErrorHints.GetHint("ErrorAccessDenied", 403, false, ""));
            Assert.Equal(ErrorHints.MailboxNotFoundHint, ErrorHints.GetHint("ResourceNotFound", 404, true, ""));
            Assert.Equal("", ErrorHints.GetHint("Validation", 403, true, "AADSTS7000215"));
            Assert.Equal("", ErrorHints.GetHint("Transport", 0, false, ""));
            Assert.Equal("", ErrorHints.GetHint("Unknown", 500, false, "boom"));
            Assert.Equal("", ErrorHints.GetHint(null, 0, false, null));
        }

        [Fact]
        public void Aadsts_code_found_in_message()
        {
            string hint = ErrorHints.GetHint("Token", 0, false, "MsalServiceException: AADSTS7000222: The provided client secret keys are expired.");
            Assert.Contains("expired", hint);
            Assert.Equal("", ErrorHints.GetHint("Token", 0, false, "AADSTS12345: unknown code"));
        }

        [Fact]
        public void Append_adds_once()
        {
            Assert.Equal("err" + ErrorHints.Prefix + "do x", ErrorHints.Append("err", "do x"));
            Assert.Equal("err", ErrorHints.Append("err", ""));
            Assert.Equal("err Hint: a", ErrorHints.Append("err Hint: a", "b"));
            Assert.Equal(" Hint: x", ErrorHints.Append(null!, "x"));
        }
    }
}
