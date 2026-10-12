using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NavMailGraph.Internal
{
    /// <summary>
    /// Maps Microsoft Graph error codes, HTTP statuses and Entra AADSTS codes to a short configuration hint that is
    /// appended to the error text returned by <see cref="GraphMailSender.Send"/> / <see cref="GraphMailSender.TestConnection"/>
    /// as " Hint: ...". The list follows docs/DESIGN.md section 11; the codes are documented in docs/research-notes.md.
    /// Pure functions over a static table so they can be unit-tested without HTTP.
    /// </summary>
    internal static class ErrorHints
    {
        /// <summary>Separator placed between the error text and the hint.</summary>
        internal const string Prefix = " Hint: ";

        internal const string MailReadWriteHint =
            "The draft/large-attachment flow requires Mail.ReadWrite (Exchange RBAC role 'Application Mail.ReadWrite') in addition to Mail.Send. "
            + "Grant it, or set UseDraftForLargeMessages to false and keep messages below the sendMail size limit.";

        internal const string AccessDeniedHint =
            "The app is not allowed to send from this mailbox: check that the mailbox is inside the scope of the Exchange RBAC role assignment "
            + "'Application Mail.Send' (or that the Mail.Send application permission is granted with admin consent). Permission changes can take 30 minutes to 2 hours to apply.";

        internal const string SendAsDeniedHint =
            "The From address differs from the sending mailbox and that mailbox has no Send As right for it: use the Graph Sender Mailbox setting "
            + "(the From address then becomes the mailbox) or grant Send As.";

        internal const string MailboxNotFoundHint =
            "The sending mailbox does not exist or has no Exchange Online mailbox/license: check the Graph Sender Mailbox or the From address.";

        internal const string PayloadTooLargeHint =
            "The request exceeded the Microsoft Graph request size limit (4 MB): lower SendMailPayloadLimitBytes/DirectAttachmentLimitBytes "
            + "or enable UseDraftForLargeMessages.";

        internal const string UploadSessionMinimumSizeHint =
            "Attachments below 3 MB cannot use an upload session: do not set DirectAttachmentLimitBytes below 3 MB.";

        /// <summary>Graph error codes and AADSTS codes (case-insensitive, exact match) -> hint.</summary>
        internal static readonly KeyValuePair<string, string>[] CodeTable =
        {
            // Microsoft Graph error.code values
            Entry("ErrorAccessDenied", AccessDeniedHint),
            Entry("ErrorSendAsDenied", SendAsDeniedHint),
            Entry("MailboxNotEnabledForRESTAPI", MailboxNotFoundHint),
            Entry("ResourceNotFound", MailboxNotFoundHint),
            Entry("ErrorAttachmentSizeShouldNotBeLessThanMinimumSize", UploadSessionMinimumSizeHint),

            // Entra ID (AADSTS) codes from the token endpoint
            Entry("AADSTS7000215", "Wrong client secret: enter the secret's Value, not its Secret ID."),
            Entry("AADSTS7000222", "The client secret has expired: create a new secret in the app registration and update the setup."),
            Entry("AADSTS700016", "The Client ID was not found in this tenant: check Graph Client ID and Graph Tenant ID."),
            Entry("AADSTS90002", "The tenant was not found: check Graph Tenant ID (GUID or verified domain) and the Cloud setting."),
            Entry("AADSTS700027", "The certificate is not registered on the app registration or a different certificate was used: upload the public certificate (.cer) to the app and check the thumbprint."),
            Entry("AADSTS50059", "The tenant is missing: set Graph Tenant ID to the tenant GUID or verified domain ('common' is not supported for client credentials)."),
            Entry("AADSTS9001023", "The tenant is missing: set Graph Tenant ID to the tenant GUID or verified domain ('common' is not supported for client credentials)."),
        };

        /// <summary>HTTP status -> hint, used when the error code has no entry.</summary>
        internal static readonly KeyValuePair<int, string>[] StatusTable =
        {
            new KeyValuePair<int, string>(403, AccessDeniedHint),
            new KeyValuePair<int, string>(404, MailboxNotFoundHint),
            new KeyValuePair<int, string>(413, PayloadTooLargeHint),
        };

        private static readonly Regex AadstsPattern = new Regex(@"AADSTS\d+", RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns the hint for an error, or "" when there is none.
        /// Precedence: ErrorSendAsDenied; a 403 in the draft flow (Mail.ReadWrite); the code table; an AADSTS code found in
        /// <paramref name="message"/> (e.g. from the MSAL provider); the status table.
        /// </summary>
        /// <param name="errorCode">Graph error.code, AADSTS code or our own code ("Validation", "Transport", ...).</param>
        /// <param name="statusCode">HTTP status, 0 if none.</param>
        /// <param name="requiresMailReadWrite">True when the failed call belongs to the draft flow operations that need Mail.ReadWrite.</param>
        /// <param name="message">The error text (searched for an AADSTS code).</param>
        internal static string GetHint(string? errorCode, int statusCode, bool requiresMailReadWrite, string? message)
        {
            string code = (errorCode ?? "").Trim();
            if (string.Equals(code, GraphMailException.ValidationCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, GraphMailException.TransportCode, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (string.Equals(code, "ErrorSendAsDenied", StringComparison.OrdinalIgnoreCase))
            {
                return SendAsDeniedHint;
            }

            if (requiresMailReadWrite && statusCode == 403)
            {
                return MailReadWriteHint;
            }

            string? hint = LookupCode(code);
            if (hint != null)
            {
                return hint;
            }

            if (!string.IsNullOrEmpty(message))
            {
                Match match = AadstsPattern.Match(message);
                if (match.Success)
                {
                    hint = LookupCode(match.Value);
                    if (hint != null)
                    {
                        return hint;
                    }
                }
            }

            foreach (KeyValuePair<int, string> entry in StatusTable)
            {
                if (entry.Key == statusCode)
                {
                    return entry.Value;
                }
            }

            return "";
        }

        /// <summary>Appends " Hint: {hint}" to <paramref name="text"/> unless the hint is empty or already present.</summary>
        internal static string Append(string text, string hint)
        {
            if (string.IsNullOrEmpty(hint) || (text ?? "").IndexOf(Prefix, StringComparison.Ordinal) >= 0)
            {
                return text ?? "";
            }

            return (text ?? "") + Prefix + hint;
        }

        private static string? LookupCode(string code)
        {
            if (code.Length == 0)
            {
                return null;
            }

            foreach (KeyValuePair<string, string> entry in CodeTable)
            {
                if (string.Equals(entry.Key, code, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private static KeyValuePair<string, string> Entry(string code, string hint)
        {
            return new KeyValuePair<string, string>(code, hint);
        }
    }
}
