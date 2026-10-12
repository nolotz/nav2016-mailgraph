using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NavMailGraph.Cli
{
    /// <summary>
    /// 'nmg inbox ...': reads a mailbox with <see cref="GraphMailbox"/> (docs/DESIGN-v2.md sections 2.2 and 5.1). Uses only the
    /// public library API, exactly as NAV does; error texts are printed as the library returns them.
    /// </summary>
    internal static class InboxCommands
    {
        private const int ScanBatch = 1000;

        private const int MaxScanRounds = 1000;

        internal static int Run(List<string> args)
        {
            if (args.Count == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            {
                throw new UsageException("missing inbox subcommand: list, get, save-attachment, save-eml, mark-read, mark-unread, move or delta.");
            }

            string sub = args[0];
            List<string> rest = args.Skip(1).ToList();
            switch (sub)
            {
                case "list":
                    return List(Parse(sub, rest, 0, 1));
                case "get":
                    return Get(Parse(sub, rest, 1, 1));
                case "save-attachment":
                    return SaveAttachment(Parse(sub, rest, 3, 3));
                case "save-eml":
                    return SaveEml(Parse(sub, rest, 2, 2));
                case "mark-read":
                    return Mark(Parse(sub, rest, 1, 1), true);
                case "mark-unread":
                    return Mark(Parse(sub, rest, 1, 1), false);
                case "move":
                    return Move(Parse(sub, rest, 2, 2));
                case "delta":
                    return Delta(Parse(sub, rest, 0, 0));
                default:
                    throw new UsageException("unknown inbox subcommand '" + sub + "'.");
            }
        }

        private static readonly string[] Usages =
        {
            "list [FOLDER]", "get ID", "save-attachment ID ATTACHMENT-ID PATH", "save-eml ID PATH", "mark-read ID", "mark-unread ID",
            "move ID FOLDER-PATH", "delta [--link-file PATH]",
        };

        private static CliOptions Parse(string sub, List<string> rest, int minPositionals, int maxPositionals)
        {
            CliOptions options = CliOptions.Parse("inbox " + sub, rest, OptionScope.Auth | OptionScope.Inbox, maxPositionals);
            if (options.Positionals.Count < minPositionals)
            {
                string usage = Usages.First(u => u.StartsWith(sub + " ", StringComparison.Ordinal) || u == sub);
                throw new UsageException("usage: nmg inbox " + usage + " [options].");
            }

            RejectFromCommandLine(options, sub, "unread", "list");
            RejectFromCommandLine(options, sub, "max", "list", "delta");
            RejectFromCommandLine(options, sub, "since", "list", "get", "delta");
            RejectFromCommandLine(options, sub, "full-body", "get");
            RejectFromCommandLine(options, sub, "body-text", "get");
            RejectFromCommandLine(options, sub, "link-file", "delta");
            return options;
        }

        private static void RejectFromCommandLine(CliOptions options, string sub, string option, params string[] allowed)
        {
            if (options.FromCommandLine.Contains(option) && !allowed.Contains(sub))
            {
                throw new UsageException("option '--" + option + "' is only valid for 'nmg inbox " + string.Join("|", allowed) + "'.");
            }
        }

        private static GraphMailbox CreateMailbox(CliOptions options)
        {
            string mailbox = options.Require("mailbox").Trim();
            GraphConnection connection = Program.CreateConnection(options);
            var reader = new GraphMailbox(connection, mailbox);
            if (options.Has("folder"))
            {
                reader.Folder = options.Get("folder");
            }

            int max = options.GetInt("max", reader.MaxMessages);
            if (max < 1 || max > 1000)
            {
                throw new UsageException("option '--max' must be between 1 and 1000, got '" + options.Get("max") + "'.");
            }

            reader.MaxMessages = max;
            reader.ReceivedAfter = options.Get("since");
            return reader;
        }

        // ----------------------------------------------------------------------------------------------------------
        // list / delta
        // ----------------------------------------------------------------------------------------------------------

        private static int List(CliOptions options)
        {
            GraphMailbox reader = CreateMailbox(options);
            if (options.Positionals.Count > 0)
            {
                reader.Folder = options.Positionals[0];
            }

            reader.UnreadOnly = options.Flag("unread");
            reader.IncludeBody = false;
            string error = reader.Fetch();
            if (error.Length > 0)
            {
                return Program.Report(reader.LastDiagnostics, options, error, "");
            }

            PrintTable(reader);
            string summary = reader.Count + " message(s) in '" + reader.Folder + "' of " + reader.Mailbox + ".";
            if (reader.MoreAvailable)
            {
                summary += " More are available: continue with --since " + reader.GetMessage(reader.Count - 1).ReceivedText
                    + " (inclusive, the last one repeats) or raise --max.";
            }

            return Program.Report(reader.LastDiagnostics, options, "", summary);
        }

        private static int Delta(CliOptions options)
        {
            GraphMailbox reader = CreateMailbox(options);
            reader.IncludeBody = false;
            string linkFile = options.Get("link-file");
            string link = "";
            if (linkFile.Length > 0)
            {
                if (File.Exists(linkFile))
                {
                    try
                    {
                        link = File.ReadAllText(linkFile).Trim();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new UsageException("cannot read --link-file '" + linkFile + "': " + ex.Message);
                    }
                }

                Console.Out.WriteLine(link.Length == 0
                    ? "No delta link in '" + linkFile + "': starting a new sync of '" + reader.Folder + "'" + (reader.ReceivedAfter.Length > 0 ? " from " + reader.ReceivedAfter : "") + "."
                    : "Continuing with the delta link from '" + linkFile + "' (--folder and --since do not apply).");
            }

            string error = reader.FetchChanges(link);
            if (error.Length > 0)
            {
                if (reader.DeltaExpired)
                {
                    error += Environment.NewLine + (linkFile.Length > 0
                        ? "nmg: the stored delta link is no longer valid; delete '" + linkFile + "' and run again (with --since to bound the new sync)."
                        : "nmg: start again without a link (with --since to bound the new sync).");
                }

                return Program.Report(reader.LastDiagnostics, options, error, "");
            }

            PrintTable(reader);
            for (int i = 0; i < reader.RemovedCount; i++)
            {
                Console.Out.WriteLine("removed: " + reader.GetRemovedId(i));
            }

            var summary = new StringBuilder();
            summary.Append(reader.Count + " new message(s), " + reader.RemovedCount + " removed.");
            if (reader.MoreAvailable)
            {
                summary.Append(" More changes are available: run again with the stored link.");
            }

            if (linkFile.Length > 0)
            {
                string writeError = WriteLinkFile(linkFile, reader.NextDeltaLink);
                if (writeError.Length > 0)
                {
                    // Runtime IO failure after the changes were read (exit 1, not a usage error): keep the link on stdout
                    // so it is not lost.
                    Console.Out.WriteLine(summary.ToString());
                    Console.Out.WriteLine("Next link (not stored): " + reader.NextDeltaLink);
                    return Program.Report(reader.LastDiagnostics, options, writeError, "");
                }

                summary.Append(" Next link stored in '" + linkFile + "'.");
            }
            else
            {
                summary.Append(Environment.NewLine + "Next link (store it in a file and pass --link-file FILE to continue): " + reader.NextDeltaLink);
            }

            return Program.Report(reader.LastDiagnostics, options, "", summary.ToString());
        }

        /// <summary>Writes the delta link atomically; returns "" or an error text (never a usage error).</summary>
        private static string WriteLinkFile(string path, string link)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, link + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temp, path, true);
                return "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException || cleanup is NotSupportedException || cleanup is ArgumentException)
                {
                    // best effort
                }

                return "nmg: the changes were read, but the next delta link could not be written to '" + path + "': " + ex.Message;
            }
        }

        private static void PrintTable(GraphMailbox reader)
        {
            if (reader.Count == 0)
            {
                return;
            }

            var rows = new List<string[]>();
            var warnings = new List<string>();
            for (int i = 0; i < reader.Count; i++)
            {
                GraphMailItem item = reader.GetMessage(i);
                string attachments = "0";
                if (item.HasAttachments)
                {
                    int count = item.AttachmentCount; // one Graph call per message with attachments
                    attachments = count >= 0 ? count.ToString(CultureInfo.InvariantCulture) : "?";
                    if (count < 0)
                    {
                        warnings.Add("attachments of " + item.Id + ": " + item.AttachmentError);
                    }
                }

                rows.Add(new[]
                {
                    item.ReceivedText,
                    (item.IsRead ? " " : "*") + attachments,
                    Clip(item.FromAddress.Length > 0 ? item.FromAddress : item.SenderAddress, 32),
                    Clip(item.Subject, 50),
                    item.Id,
                });
            }

            string[] header = { "RECEIVED (UTC)", " ATT", "FROM", "SUBJECT", "ID" };
            int[] widths = new int[header.Length - 1];
            for (int c = 0; c < widths.Length; c++)
            {
                widths[c] = Math.Max(header[c].Length, rows.Max(r => r[c].Length));
            }

            Console.Out.WriteLine(FormatRow(header, widths));
            foreach (string[] row in rows)
            {
                Console.Out.WriteLine(FormatRow(row, widths));
            }

            Console.Out.WriteLine("(* = unread)");
            foreach (string warning in warnings)
            {
                Console.Error.WriteLine("nmg: warning: " + warning);
            }
        }

        private static string FormatRow(string[] cells, int[] widths)
        {
            var line = new StringBuilder();
            for (int c = 0; c < cells.Length; c++)
            {
                line.Append(c < widths.Length ? cells[c].PadRight(widths[c] + 2) : cells[c]);
            }

            return line.ToString();
        }

        /// <summary>Single line, at most <paramref name="max"/> characters.</summary>
        private static string Clip(string text, int max)
        {
            var clean = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                clean.Append(char.IsControl(ch) ? ' ' : ch);
            }

            string value = clean.ToString().Trim();
            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }

        // ----------------------------------------------------------------------------------------------------------
        // get
        // ----------------------------------------------------------------------------------------------------------

        private static int Get(CliOptions options)
        {
            string id = options.Positionals[0].Trim();
            GraphMailbox scanner = CreateMailbox(options);
            scanner.IncludeBody = false;
            scanner.MaxMessages = ScanBatch;
            GraphMailItem? found = null;
            string watermark = scanner.ReceivedAfter;
            int scanned = 0;
            for (int round = 0; round < MaxScanRounds && found == null; round++)
            {
                string error = scanner.Fetch();
                if (error.Length > 0)
                {
                    return Program.Report(scanner.LastDiagnostics, options, error, "");
                }

                for (int i = 0; i < scanner.Count; i++)
                {
                    if (string.Equals(scanner.GetMessage(i).Id, id, StringComparison.Ordinal))
                    {
                        found = scanner.GetMessage(i);
                        break;
                    }
                }

                scanned += scanner.Count;
                if (found != null || !scanner.MoreAvailable)
                {
                    break;
                }

                // Page through the folder with the watermark (inclusive, so the last message of a batch repeats).
                string last = scanner.GetMessage(scanner.Count - 1).ReceivedText;
                if (string.Equals(last, watermark, StringComparison.Ordinal))
                {
                    return Program.Report(scanner.LastDiagnostics, options, "nmg: more than " + ScanBatch + " messages were received at " + last
                        + "; cannot search further. Use --since to start after that time.", "");
                }

                watermark = last;
                scanner.ReceivedAfter = last;
            }

            if (found == null)
            {
                return Program.Report(scanner.LastDiagnostics, options, "Message " + id + " was not found in folder '" + scanner.Folder + "' of "
                    + scanner.Mailbox + " (" + scanned + " message(s) checked" + (options.Has("since") ? " from " + options.Get("since") : "")
                    + "). Use --folder with the folder that contains it; ids are case-sensitive.", "");
            }

            // Load the body (for the preview, inline cid: attachments and --full-body) with a small fetch at the message's time.
            GraphMailItem item = found;
            GraphMailbox detail = CreateMailbox(options);
            detail.Folder = scanner.Folder;
            detail.ReceivedAfter = found.ReceivedText;
            detail.MaxMessages = 50;
            detail.IncludeBody = true;
            detail.BodyAsText = options.Flag("body-text");
            GraphMailbox owner = scanner;
            if (detail.Fetch().Length == 0)
            {
                for (int i = 0; i < detail.Count; i++)
                {
                    if (string.Equals(detail.GetMessage(i).Id, id, StringComparison.Ordinal))
                    {
                        item = detail.GetMessage(i);
                        owner = detail;
                        break;
                    }
                }
            }

            PrintMessage(item, options.Flag("full-body"), !ReferenceEquals(item, found));
            string attachmentError = item.LoadAttachments();
            if (attachmentError.Length > 0)
            {
                return Program.Report(owner.LastDiagnostics, options, attachmentError, "");
            }

            PrintAttachments(item);
            return Program.Report(owner.LastDiagnostics, options, "", "");
        }

        private static void PrintMessage(GraphMailItem item, bool fullBody, bool bodyLoaded)
        {
            Field("Id", item.Id);
            Field("Internet message id", item.InternetMessageId);
            Field("Conversation id", item.ConversationId);
            Field("Received (UTC)", item.ReceivedText);
            Field("Sent (UTC)", item.SentText);
            Field("From", NameAddress(item.FromName, item.FromAddress));
            if (!string.Equals(item.SenderAddress, item.FromAddress, StringComparison.OrdinalIgnoreCase))
            {
                Field("Sender", NameAddress(item.SenderName, item.SenderAddress));
            }

            Field("To", item.ToRecipients);
            Field("Cc", item.CcRecipients);
            Field("Reply-To", item.ReplyTo);
            Field("Subject", item.Subject);
            Field("Importance", item.Importance);
            Field("Read", item.IsRead ? "yes" : "no");
            Field("Web link", item.WebLink);
            Console.Out.WriteLine();
            if (fullBody && bodyLoaded)
            {
                Console.Out.WriteLine("Body (" + (item.BodyIsHtml ? "HTML" : "text") + ", " + item.Body.Length + " characters):");
                Console.Out.WriteLine(item.Body);
            }
            else
            {
                if (fullBody)
                {
                    Console.Error.WriteLine("nmg: warning: the body could not be loaded; showing the preview.");
                }

                Console.Out.WriteLine("Preview" + (bodyLoaded ? " (body: " + (item.BodyIsHtml ? "HTML" : "text") + ", " + item.Body.Length + " characters; --full-body prints it)" : "") + ":");
                Console.Out.WriteLine(item.BodyPreview);
            }

            Console.Out.WriteLine();
        }

        private static void PrintAttachments(GraphMailItem item)
        {
            int count = item.AttachmentCount;
            Console.Out.WriteLine("Attachments: " + count);
            for (int i = 0; i < count; i++)
            {
                GraphMailAttachmentInfo info = item.GetAttachment(i);
                Console.Out.WriteLine("  [" + i + "] " + info.Name + "  (" + info.Kind + (info.IsInline ? ", inline" : "") + ", "
                    + info.Size.ToString(CultureInfo.InvariantCulture) + " bytes" + (info.ContentType.Length > 0 ? ", " + info.ContentType : "")
                    + ", save as " + info.SuggestedFileName + ")");
                Console.Out.WriteLine("      id: " + info.Id);
            }
        }

        private static void Field(string name, string value)
        {
            string clean = Clip(value, int.MaxValue);
            if (clean.Length > 0)
            {
                Console.Out.WriteLine((name + ":").PadRight(21) + clean);
            }
        }

        private static string NameAddress(string name, string address)
        {
            return name.Length > 0 && !string.Equals(name, address, StringComparison.OrdinalIgnoreCase) ? name + " <" + address + ">" : address;
        }

        // ----------------------------------------------------------------------------------------------------------
        // downloads and actions
        // ----------------------------------------------------------------------------------------------------------

        private static int SaveAttachment(CliOptions options)
        {
            GraphMailbox reader = CreateMailbox(options);
            string path = options.Positionals[2];
            string error = reader.SaveAttachmentToFile(options.Positionals[0].Trim(), options.Positionals[1].Trim(), path);
            return Program.Report(reader.LastDiagnostics, options, error, "Saved attachment (" + reader.LastDownloadSize + " bytes) to " + path + ".");
        }

        private static int SaveEml(CliOptions options)
        {
            GraphMailbox reader = CreateMailbox(options);
            string path = options.Positionals[1];
            string error = reader.SaveMimeToFile(options.Positionals[0].Trim(), path);
            return Program.Report(reader.LastDiagnostics, options, error, "Saved message as MIME (" + reader.LastDownloadSize + " bytes) to " + path + ".");
        }

        private static int Mark(CliOptions options, bool read)
        {
            GraphMailbox reader = CreateMailbox(options);
            string id = options.Positionals[0].Trim();
            string error = read ? reader.MarkAsRead(id) : reader.MarkAsUnread(id);
            return Program.Report(reader.LastDiagnostics, options, error, "Marked as " + (read ? "read" : "unread") + ".");
        }

        private static int Move(CliOptions options)
        {
            GraphMailbox reader = CreateMailbox(options);
            string folder = options.Positionals[1];
            string error = reader.MoveToFolder(options.Positionals[0].Trim(), folder);
            return Program.Report(reader.LastDiagnostics, options, error, "Moved to '" + folder + "'. Message id now: " + reader.LastMovedMessageId);
        }
    }
}
