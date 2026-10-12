using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NavMailGraph.Cli
{
    /// <summary>
    /// 'nmg': command-line test tool that sends and reads mail through Microsoft Graph and uses object storage with the same
    /// library (NavMailGraph.dll) that NAV uses, so credentials, Exchange RBAC scoping, large attachments and storage settings
    /// can be proven before anything touches NAV.
    /// Exit codes: 0 success, 1 failure (the error text is printed exactly as the library returned it, i.e. as
    /// NAV would show it), 2 usage/configuration error or --help.
    /// </summary>
    internal static class Program
    {
        internal const int ExitOk = 0;

        internal const int ExitFailure = 1;

        internal const int ExitUsage = 2;

        private const string HelpHeader = @"nmg - Microsoft Graph mail and object storage with the NavMailGraph library (the one NAV uses)

Usage:
  nmg token    [auth options]                     acquire a token (GraphConnection.TestConnection)
  nmg send     [auth options] [message options]   send a mail (GraphMailSender.Send)
  nmg inbox    <subcommand> [auth options] [inbox options]     read mail (GraphMailbox)
  nmg storage  <subcommand> [storage options]     object storage (ObjectStorageClient)
  nmg selftest [--verbose]                        offline end-to-end test against an in-process fake Graph server
  nmg version                                     print CLI and library versions
  nmg [<command>] --help | -h                     help (all of it, or for one command)
";

        private const string HelpAuth = @"
Auth / transport options (token, send, inbox; storage only with --provider azure-entra):
  --tenant ID                 tenant GUID or verified domain (not common/organizations/consumers)
  --client ID                 application (client) id
  --secret-env NAME           client secret from environment variable NAME (preferred)
  --secret VALUE              client secret on the command line (ends up in shell history!)
  --cert-pfx PATH             certificate from a PFX file ...
    --cert-password-env NAME  ... with the PFX password from environment variable NAME
    --cert-password VALUE     ... or on the command line (ends up in shell history!)
  --cert-thumbprint HEX       certificate from the certificate store (LocalMachine\My, then CurrentUser\My)
  --token-library builtin|msal          default builtin (msal needs NavMailGraph.Msal.dll next to the core)
  --cloud global|usgov|usgovdod|china   default global
  --graph-url URL             override the Graph base URL, e.g. https://graph.microsoft.com/v1.0
  --authority URL             override the authority host, e.g. https://login.microsoftonline.com
  --timeout SECONDS           per Graph HTTP request; downloads: idle timeout (default 100)
  --retries N                 Graph retries on HTTP 429/503/509 (default 3)
  --send-x5c                  include the x5c claim in the certificate client assertion
  --log PATH                  append a redacted diagnostic log to PATH (Graph and storage)
  --verbose                   always print the diagnostic log (it is printed on errors anyway)
  --config PATH               JSON file with the same keys as the long options without '--'
                              (default ./nmg.local.json if present; command-line values override the file;
                              keys of other commands are ignored)
";

        private const string HelpSend = @"
Message options (nmg send):
  --from ""Name <addr>""        sender (FromName/FromAddress); also the mailbox unless --mailbox is given
  --mailbox ADDR              fixed sender mailbox (/users/{mailbox}); the mail is always sent as this mailbox
  --no-reply-to-from          with --mailbox: do not put a differing --from address into Reply-To
  --to ADDRS                  recipients, ""a@x.com; Name <b@y.com>"" (repeatable)
  --cc ADDRS / --bcc ADDRS / --reply-to ADDRS   (repeatable)
  --subject TEXT
  --body TEXT | --body-file PATH
  --html                      body is HTML
  --attach PATH               attachment, read at send time (repeatable)
  --inline PATH:CID           inline attachment referenced as cid:CID in an HTML body (repeatable)
  --importance low|normal|high
  --header ""x-name: value""    internet message header, name must start with x- (repeatable)
  --no-save-sent              do not save to Sent Items (direct sendMail path only)
  --no-draft                  never use the draft/upload flow (it needs Mail.ReadWrite); large mails fail instead
  --attach-storage KEY        attachment downloaded from object storage at send time; file name = last key
                              segment (repeatable; needs the storage options below)
  --link-storage              upload large attachments to object storage and put a download link into the body
                              instead (GraphMailSender.UseLinkStorage; needs the storage options below and a
                              provider that can create links: azure-key, azure-sas or s3)
    --link-threshold-mb N     required with --link-storage: attachments of N MiB or more become links
    --link-days N             link validity in days (default 7; S3 allows at most 7)
    --link-text FORMAT        link text, {0} = file name, {1} = size, {2} = expiry (default ""Download {0} ({1})"")
";

        private const string HelpInbox = @"
Inbox commands (nmg inbox; need Mail.Read, actions need Mail.ReadWrite):
  nmg inbox list [FOLDER]                         list messages, oldest first: id, received, from, subject,
                                                  attachment count (FOLDER overrides --folder)
  nmg inbox get ID                                headers, body preview and attachments of one message
  nmg inbox save-attachment ID ATTACHMENT-ID PATH save an attachment ($value, streamed) to PATH
  nmg inbox save-eml ID PATH                      save the message as MIME (.eml) to PATH
  nmg inbox mark-read ID | mark-unread ID         set the read flag
  nmg inbox move ID FOLDER-PATH                   move the message; missing folders of the path are created
  nmg inbox delta [--link-file PATH]              new messages since the stored delta link (changeType=created);
                                                  the next link is written to PATH (printed without --link-file)

Inbox options:
  --mailbox ADDR              mailbox to read (UPN/SMTP address or object id), required
  --folder PATH               well-known name, folder id or path like Inbox/NAV/Invoices (default inbox);
                              list, get and delta read this folder
  --since ISO                 only messages received at or after this time, e.g. 2026-10-12T08:00:00Z
                              (list, get; delta: bounds the first sync)
  --unread                    list: only unread messages
  --max N                     list/delta: at most N messages (default 50, at most 1000)
  --full-body                 get: print the whole body instead of the preview
  --body-text                 get: ask Graph for the body as text instead of HTML
  --link-file PATH            delta: file that holds the delta link (read if present, then replaced)
  'get' finds the message by scanning FOLDER (from --since on) because the library reads by folder, so use the
  folder the message is in. Ids are case-sensitive immutable ids, as printed by 'list'.
";

        private const string HelpStorage = @"
Storage commands (nmg storage):
  nmg storage test                    check configuration, credentials and container/bucket (lists one object)
  nmg storage put FILE [KEY]          upload FILE (KEY default: the file name); --content-type TYPE (default by extension)
  nmg storage get KEY PATH            download KEY to PATH
  nmg storage ls [PREFIX]             list objects (size and key)
  nmg storage rm KEY                  delete KEY (a missing object counts as success)
  nmg storage url KEY [--minutes N]   time-limited download link (default 60 minutes; S3 at most 7 days;
                                      azure-sas: the SAS expiry applies; not available with azure-entra)
  nmg storage size KEY                object size in bytes (exit 1 when it does not exist)

Storage options (nmg storage; nmg send with --link-storage/--attach-storage):
  --provider azure-key|azure-sas|azure-entra|s3
  --account NAME              Azure storage account (azure-key, azure-entra)
  --account-key-env NAME      azure-key: account key (key1/key2) from environment variable NAME
  --container NAME            Azure container (azure-key, azure-entra)
  --sas-url-env NAME          azure-sas: container SAS URL from environment variable NAME
  --endpoint URL              Azure: account URL override (Azurite: http://127.0.0.1:10000/devstoreaccount1);
                              S3: service URL (default AWS https://s3.{region}.amazonaws.com)
  --blob-suffix SUFFIX        Azure endpoint suffix (default blob.core.windows.net)
  --region NAME               S3 region (default us-east-1)
  --bucket NAME               S3 bucket
  --access-key ID             S3 access key id
  --secret-key-env NAME       S3 secret access key from environment variable NAME
  --session-token-env NAME    S3 session token of temporary credentials from environment variable NAME
  --path-style auto|on|off    S3 addressing: on = path-style, off = virtual-hosted style; auto (default) = path-style
                              unless the endpoint is amazonaws.com (or empty) and the bucket has no dot
  --prefix TEXT               key prefix prepended to every key, e.g. nav/prod/ (listed keys are shown without it)
  --threshold-mb N            single PUT up to N MiB, larger uploads in blocks/parts (default 64)
  --part-mb N                 block/part size in MiB (default 8; S3 minimum 5)
  --storage-timeout SECONDS   per storage request; downloads: idle timeout (default 300)
  azure-entra uses the auth options (--tenant, --client, a credential, --cloud global|china); the app needs the
  RBAC role 'Storage Blob Data Contributor'. Secrets are only read from environment variables.
";

        private const string HelpFooter = @"
Exit codes: 0 success, 1 failure (error text as NAV shows it), 2 usage error or --help.
Examples:
  NMG_SECRET=... nmg send --tenant contoso.onmicrosoft.com --client 1111... --secret-env NMG_SECRET \
      --from nav@contoso.com --to me@contoso.com --subject Test --body Hello --attach ./big.pdf
  nmg inbox list --mailbox invoices@contoso.com --folder Inbox/NAV --since 2026-10-01T00:00:00Z
  AZ_KEY=... nmg storage put ./invoice.pdf in/invoice.pdf --provider azure-key --account navfiles \
      --account-key-env AZ_KEY --container nav
";

        private static string Usage
        {
            get { return HelpHeader + HelpAuth + HelpSend + HelpInbox + HelpStorage + HelpFooter; }
        }

        /// <summary>Help text for one command ("" = everything).</summary>
        internal static string HelpFor(string command)
        {
            switch (command)
            {
                case "token":
                    return HelpHeader + HelpAuth + HelpFooter;
                case "send":
                    return HelpHeader + HelpAuth + HelpSend + HelpStorage + HelpFooter;
                case "inbox":
                    return HelpHeader + HelpInbox + HelpAuth + HelpFooter;
                case "storage":
                    return HelpHeader + HelpStorage + HelpAuth + HelpFooter;
                default:
                    return Usage;
            }
        }

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("nmg: " + ex.Message);
                Console.Error.WriteLine("Run 'nmg " + (KnownCommand(args) ? args[0] + " " : "") + "--help' for usage.");
                return ExitUsage;
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                throw new UsageException("missing command.");
            }

            if (args[0] == "help" || args.Any(a => a == "--help" || a == "-h"))
            {
                string topic = args[0] == "help" ? (args.Length > 1 ? args[1] : "") : (KnownCommand(args) ? args[0] : "");
                Console.Out.Write(HelpFor(topic));
                return ExitUsage;
            }

            string command = args[0];
            List<string> rest = args.Skip(1).ToList();
            switch (command)
            {
                case "version":
                case "--version":
                    if (rest.Count > 0)
                    {
                        throw new UsageException("'nmg version' takes no options.");
                    }

                    Console.Out.WriteLine("nmg " + CliVersion() + " (NavMailGraph " + GraphMailSender.Version + ")");
                    return ExitOk;
                case "token":
                    return RunToken(CliOptions.Parse(command, rest, OptionScope.Auth, 0));
                case "send":
                    return RunSend(CliOptions.Parse(command, rest, OptionScope.Auth | OptionScope.Send | OptionScope.Storage, 0));
                case "inbox":
                    return InboxCommands.Run(rest);
                case "storage":
                    return StorageCommands.Run(rest);
                case "selftest":
                    foreach (string arg in rest)
                    {
                        if (arg != "--verbose")
                        {
                            throw new UsageException("'nmg selftest' only accepts --verbose (it runs offline and ignores auth options).");
                        }
                    }

                    return SelfTest.Run(rest.Count > 0);
                default:
                    throw new UsageException(command.StartsWith("-", StringComparison.Ordinal) ? "missing command before '" + command + "'." : "unknown command '" + command + "'.");
            }
        }

        private static bool KnownCommand(string[] args)
        {
            return args.Length > 0 && (args[0] == "token" || args[0] == "send" || args[0] == "inbox" || args[0] == "storage");
        }

        private static int RunToken(CliOptions options)
        {
            GraphConnection connection = CreateConnection(options);
            string error = connection.TestConnection();
            return Report(connection.LastDiagnostics, options, error, "Token acquired.");
        }

        private static int RunSend(CliOptions options)
        {
            if (options.GetAll("to").All(v => v.Trim().Length == 0))
            {
                throw new UsageException("missing required option '--to'.");
            }

            if (options.Get("from").Trim().Length == 0 && options.Get("mailbox").Trim().Length == 0)
            {
                throw new UsageException("missing sender: give '--from' and/or '--mailbox'.");
            }

            if (options.Has("body") && options.Has("body-file"))
            {
                throw new UsageException("use either '--body' or '--body-file', not both.");
            }

            bool linkStorage = options.Flag("link-storage");
            if (!linkStorage && (options.FromCommandLine.Contains("link-threshold-mb") || options.FromCommandLine.Contains("link-days") || options.FromCommandLine.Contains("link-text")))
            {
                throw new UsageException("--link-threshold-mb, --link-days and --link-text need --link-storage.");
            }

            long linkThreshold = 0;
            int linkDays = 7;
            if (linkStorage)
            {
                if (!options.Has("link-threshold-mb"))
                {
                    throw new UsageException("--link-storage needs --link-threshold-mb N (attachments of N MiB or more become links).");
                }

                linkThreshold = options.GetMegabytes("link-threshold-mb", 0);
                linkDays = options.GetInt("link-days", 7);
                if (linkDays < 1 || linkDays > 365)
                {
                    throw new UsageException("--link-days needs a number of days between 1 and 365, got '" + options.Get("link-days") + "'.");
                }
            }

            ObjectStorageClient? storage = linkStorage || options.GetAll("attach-storage").Count > 0 ? StorageCommands.CreateClient(options) : null;

            GraphMailSender sender = CreateSender(options);
            sender.SenderMailbox = options.Get("mailbox");
            sender.SaveToSentItems = !options.Flag("no-save-sent");
            sender.ReplyToFromAddress = !options.Flag("no-reply-to-from");
            sender.UseDraftForLargeMessages = !options.Flag("no-draft");
            if (linkStorage)
            {
                sender.UseLinkStorage(storage!, linkThreshold, linkDays * 24 * 60, options.Get("link-text"));
            }

            GraphMailMessage message = BuildMessage(options, storage);
            string error = sender.Send(message);
            string success = "Mail sent: " + message.Describe();
            if (error.Length == 0 && sender.LastMessageId.Length > 0)
            {
                success += Environment.NewLine + "Draft flow used, message id: " + sender.LastMessageId;
            }

            int exit = Report(sender.LastDiagnostics, options, error, success);
            if (storage != null && (options.Flag("verbose") || error.Length > 0))
            {
                Console.Error.WriteLine("storage: " + storage.Describe());
            }

            return exit;
        }

        private static GraphMailMessage BuildMessage(CliOptions options, ObjectStorageClient? storage)
        {
            var message = new GraphMailMessage();
            string fromName;
            string fromAddress;
            SplitNameAddress(options.Get("from"), out fromName, out fromAddress);
            message.FromName = fromName;
            message.FromAddress = fromAddress;
            message.Subject = options.Get("subject");
            if (options.Has("body-file"))
            {
                string path = options.Get("body-file");
                try
                {
                    message.Body = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new UsageException("cannot read --body-file '" + path + "': " + ex.Message);
                }
            }
            else
            {
                message.Body = options.Get("body");
            }

            message.HtmlFormatted = options.Flag("html");
            message.Importance = options.Get("importance");
            foreach (string value in options.GetAll("to"))
            {
                message.AddRecipients(value);
            }

            foreach (string value in options.GetAll("cc"))
            {
                message.AddCC(value);
            }

            foreach (string value in options.GetAll("bcc"))
            {
                message.AddBCC(value);
            }

            foreach (string value in options.GetAll("reply-to"))
            {
                message.AddReplyTo(value);
            }

            foreach (string path in options.GetAll("attach"))
            {
                message.AddAttachment(path, "");
            }

            foreach (string key in options.GetAll("attach-storage"))
            {
                if (key.Trim().Length == 0 || key.EndsWith("/", StringComparison.Ordinal))
                {
                    throw new UsageException("--attach-storage needs an object key such as invoices/2026/a.pdf, got '" + key + "'.");
                }

                message.AddAttachmentFromStorage(storage!, key, "");
            }

            foreach (string value in options.GetAll("inline"))
            {
                // Split at the LAST ':' so Windows paths such as C:\logo.png:logo work.
                int colon = value.LastIndexOf(':');
                if (colon <= 0 || colon == value.Length - 1)
                {
                    throw new UsageException("--inline needs PATH:CID, got '" + value + "'.");
                }

                message.AddInlineAttachment(value.Substring(0, colon), "", value.Substring(colon + 1));
            }

            foreach (string value in options.GetAll("header"))
            {
                int colon = value.IndexOf(':');
                if (colon <= 0)
                {
                    throw new UsageException("--header needs \"x-name: value\", got '" + value + "'.");
                }

                message.AddHeader(value.Substring(0, colon).Trim(), value.Substring(colon + 1).Trim());
            }

            return message;
        }

        /// <summary>"Name &lt;addr&gt;" -> (Name, addr); "addr" -> ("", addr).</summary>
        internal static void SplitNameAddress(string value, out string name, out string address)
        {
            string text = (value ?? "").Trim();
            int open = text.LastIndexOf('<');
            if (open >= 0 && text.EndsWith(">", StringComparison.Ordinal))
            {
                name = text.Substring(0, open).Trim().Trim('"').Trim();
                address = text.Substring(open + 1, text.Length - open - 2).Trim();
                return;
            }

            name = "";
            address = text;
        }

        private static GraphMailSender CreateSender(CliOptions options)
        {
            GraphConnection connection = CreateConnection(options);
            var sender = new GraphMailSender(options.Get("tenant").Trim(), options.Get("client").Trim());
            sender.UseConnection(connection);
            return sender;
        }

        /// <summary>Builds the Graph connection (tenant, client, credential, cloud, transport) from the auth options.</summary>
        internal static GraphConnection CreateConnection(CliOptions options)
        {
            string tenant = options.Require("tenant").Trim();
            string client = options.Require("client").Trim();
            var connection = new GraphConnection(tenant, client);

            ConfigureCredential(connection, options);
            connection.TokenLibrary = MapChoice(options, "token-library", new Dictionary<string, string> { { "builtin", "BuiltIn" }, { "msal", "Msal" } }, "BuiltIn");
            connection.Cloud = MapChoice(options, "cloud", new Dictionary<string, string> { { "global", "Global" }, { "usgov", "USGov" }, { "usgovdod", "USGovDoD" }, { "china", "China" } }, "Global");
            if (options.Has("graph-url"))
            {
                connection.GraphBaseUrl = options.Get("graph-url");
            }

            if (options.Has("authority"))
            {
                connection.AuthorityHost = options.Get("authority");
            }

            connection.TimeoutSeconds = options.GetInt("timeout", connection.TimeoutSeconds);
            connection.MaxRetries = options.GetInt("retries", connection.MaxRetries);
            connection.SendX5C = options.Flag("send-x5c");
            if (options.Has("log"))
            {
                connection.LogFilePath = options.Get("log");
            }

            return connection;
        }

        private static void ConfigureCredential(GraphConnection connection, CliOptions options)
        {
            string[] kinds = { "secret-env", "secret", "cert-pfx", "cert-thumbprint" };
            string[] given = kinds.Where(options.Has).ToArray();
            if (given.Length == 0)
            {
                throw new UsageException("missing credential: give one of --secret-env, --secret, --cert-pfx or --cert-thumbprint.");
            }

            if (given.Length > 1)
            {
                throw new UsageException("give exactly one credential, got --" + string.Join(" and --", given) + ".");
            }

            if ((options.Has("cert-password") || options.Has("cert-password-env")) && given[0] != "cert-pfx")
            {
                throw new UsageException("--cert-password/--cert-password-env only apply to --cert-pfx.");
            }

            switch (given[0])
            {
                case "secret-env":
                    connection.UseClientSecret(FromEnvironment(options.Get("secret-env"), "secret-env"));
                    break;
                case "secret":
                    WarnCommandLineSecret(options, "secret", "--secret-env NAME");
                    connection.UseClientSecret(options.Get("secret"));
                    break;
                case "cert-pfx":
                    if (options.Has("cert-password") && options.Has("cert-password-env"))
                    {
                        throw new UsageException("use either --cert-password or --cert-password-env, not both.");
                    }

                    string password = "";
                    if (options.Has("cert-password-env"))
                    {
                        password = FromEnvironment(options.Get("cert-password-env"), "cert-password-env");
                    }
                    else if (options.Has("cert-password"))
                    {
                        WarnCommandLineSecret(options, "cert-password", "--cert-password-env NAME");
                        password = options.Get("cert-password");
                    }

                    connection.UseCertificateFile(options.Get("cert-pfx"), password);
                    break;
                default:
                    connection.UseCertificateThumbprint(options.Get("cert-thumbprint"));
                    break;
            }
        }

        internal static void WarnCommandLineSecret(CliOptions options, string name, string alternative)
        {
            if (options.FromCommandLine.Contains(name))
            {
                Console.Error.WriteLine("nmg: warning: --" + name + " exposes the value in shell history and process listings; prefer " + alternative + ".");
            }
            else
            {
                Console.Error.WriteLine("nmg: warning: '" + name + "' is stored in plain text in " + options.ConfigFile + "; prefer " + alternative + ".");
            }
        }

        internal static string FromEnvironment(string variable, string option)
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            if (value.Length == 0)
            {
                throw new UsageException("--" + option + ": environment variable '" + variable + "' is not set or empty.");
            }

            return value;
        }

        internal static string MapChoice(CliOptions options, string name, Dictionary<string, string> choices, string fallback)
        {
            string value = options.Get(name).Trim();
            if (value.Length == 0)
            {
                return fallback;
            }

            string? mapped;
            if (!choices.TryGetValue(value.ToLowerInvariant(), out mapped))
            {
                throw new UsageException("--" + name + " must be one of " + string.Join("|", choices.Keys) + ", got '" + value + "'.");
            }

            return mapped;
        }

        internal static int Report(string diagnostics, CliOptions options, string error, string success)
        {
            bool failed = error.Length > 0;
            if (options.Flag("verbose") || failed)
            {
                PrintDiagnostics(diagnostics);
            }

            if (failed)
            {
                Console.Error.WriteLine(error);
                return ExitFailure;
            }

            if (success.Length > 0)
            {
                Console.Out.WriteLine(success);
            }

            return ExitOk;
        }

        internal static void PrintDiagnostics(string diagnostics)
        {
            if (diagnostics.Length == 0)
            {
                return;
            }

            Console.Error.WriteLine("--- diagnostics ---");
            Console.Error.WriteLine(diagnostics.TrimEnd());
            Console.Error.WriteLine("-------------------");
        }

        private static string CliVersion()
        {
            var attribute = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attribute != null ? attribute.InformationalVersion : "0.0.0";
        }
    }
}
