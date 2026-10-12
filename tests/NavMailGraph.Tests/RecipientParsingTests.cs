using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace NavMailGraph.Tests
{
    public class RecipientParsingTests
    {
        private sealed class ParseResult
        {
            internal List<MailRecipient> Recipients { get; } = new List<MailRecipient>();

            internal List<string> Errors { get; } = new List<string>();
        }

        // No ValueTuple: the tests also compile for net462, where System.ValueTuple is not in-box.
        private static ParseResult Parse(string input)
        {
            var result = new ParseResult();
            EmailAddressParser.Parse(input, result.Recipients, result.Errors);
            return result;
        }

        [Fact]
        public void Splits_on_semicolon_and_comma_and_trims()
        {
            var result = Parse(" a@x.de ;b@y.de,  c@z.de ");
            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "a@x.de", "b@y.de", "c@z.de" }, result.Recipients.Select(r => r.Address).ToArray());
            Assert.All(result.Recipients, r => Assert.Equal("", r.Name));
        }

        [Fact]
        public void Parses_display_name_with_angle_brackets()
        {
            var result = Parse("Max Mustermann <max@example.com>; \"Doe, Jane\" <jane@example.com>");
            Assert.Empty(result.Errors);
            Assert.Equal(2, result.Recipients.Count);
            Assert.Equal("max@example.com", result.Recipients[0].Address);
            Assert.Equal("Max Mustermann", result.Recipients[0].Name);
            Assert.Equal("jane@example.com", result.Recipients[1].Address);
            Assert.Equal("Doe, Jane", result.Recipients[1].Name);
        }

        [Fact]
        public void Skips_empty_entries()
        {
            var result = Parse(";;a@x.de;; ,");
            Assert.Empty(result.Errors);
            Assert.Single(result.Recipients);
        }

        [Fact]
        public void Empty_or_null_input_adds_nothing()
        {
            Assert.Empty(Parse("").Recipients);
            var message = new GraphMailMessage();
            message.AddRecipients(null!);
            message.AddCC("");
            Assert.Equal(0, message.RecipientCount);
        }

        [Theory]
        [InlineData("no-at-sign")]
        [InlineData("@x.de")]
        [InlineData("a@")]
        [InlineData("a@b@c.de")]
        [InlineData("Name <not an address>")]
        public void Invalid_entries_become_validation_errors(string input)
        {
            var result = Parse(input);
            Assert.Empty(result.Recipients);
            Assert.Single(result.Errors);
            Assert.Contains("Invalid recipient", result.Errors[0]);
        }

        [Fact]
        public void Valid_entries_survive_next_to_invalid_ones()
        {
            var result = Parse("good@x.de; bad; also.good@y.de");
            Assert.Equal(2, result.Recipients.Count);
            Assert.Single(result.Errors);
            Assert.Contains("'bad'", result.Errors[0]);
        }

        [Fact]
        public void RecipientCount_counts_to_cc_and_bcc_but_not_reply_to()
        {
            var message = new GraphMailMessage();
            message.AddRecipients("a@x.de; b@x.de");
            message.AddCC("c@x.de");
            message.AddBCC("d@x.de");
            message.AddReplyTo("e@x.de");
            Assert.Equal(4, message.RecipientCount);
        }
    }
}
