using System.Collections.Generic;
using System.Text;

namespace NavMailGraph
{
    /// <summary>A parsed recipient (SMTP address + optional display name).</summary>
    internal sealed class MailRecipient
    {
        internal MailRecipient(string address, string name)
        {
            Address = address;
            Name = name;
        }

        internal string Address { get; }

        internal string Name { get; }

        internal string Describe()
        {
            return Name.Length == 0 ? Address : Name + " <" + Address + ">";
        }
    }

    /// <summary>
    /// Parses NAV-style recipient lists: entries separated by ';' or ',', each either "user@domain" or
    /// "Display Name &lt;user@domain&gt;" (quotes around the name are allowed and a quoted name may contain separators).
    /// Entries are trimmed; empty entries are skipped; entries without '@' are reported as validation errors.
    /// </summary>
    internal static class EmailAddressParser
    {
        /// <summary>Parses <paramref name="input"/>; valid recipients go to <paramref name="target"/>, problems to <paramref name="errors"/>.</summary>
        internal static void Parse(string input, List<MailRecipient> target, List<string> errors)
        {
            if (string.IsNullOrEmpty(input))
            {
                return;
            }

            foreach (string entry in SplitEntries(input))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                MailRecipient? recipient = ParseEntry(trimmed);
                if (recipient == null)
                {
                    errors.Add("Invalid recipient '" + trimmed + "': expected 'user@domain' or 'Name <user@domain>'.");
                }
                else
                {
                    target.Add(recipient);
                }
            }
        }

        /// <summary>Splits on ';' and ',' outside of double quotes.</summary>
        internal static List<string> SplitEntries(string input)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            foreach (char c in input)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    current.Append(c);
                    continue;
                }

                if (!inQuotes && (c == ';' || c == ','))
                {
                    parts.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts;
        }

        /// <summary>Parses one trimmed, non-empty entry. Returns null when the address is not plausible.</summary>
        internal static MailRecipient? ParseEntry(string entry)
        {
            string name = "";
            string address;
            int lt = entry.LastIndexOf('<');
            int gt = entry.LastIndexOf('>');
            if (lt >= 0 && gt > lt)
            {
                address = entry.Substring(lt + 1, gt - lt - 1).Trim();
                name = entry.Substring(0, lt).Trim().Trim('"').Trim();
            }
            else
            {
                address = entry.Trim('"').Trim();
            }

            return IsPlausibleAddress(address) ? new MailRecipient(address, name) : null;
        }

        /// <summary>Cheap plausibility check: exactly one '@' that is neither first nor last, and no separators/whitespace.</summary>
        internal static bool IsPlausibleAddress(string address)
        {
            int at = address.IndexOf('@');
            if (at <= 0 || at == address.Length - 1 || address.IndexOf('@', at + 1) >= 0)
            {
                return false;
            }

            foreach (char c in address)
            {
                if (char.IsWhiteSpace(c) || c == '<' || c == '>' || c == ',' || c == ';' || c == '"')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
