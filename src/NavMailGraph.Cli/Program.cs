using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NavMailGraph.Cli
{
    /// <summary>
    /// 'nmg': command-line test tool that sends mail through Microsoft Graph with the same library (NavMailGraph.dll) that
    /// NAV uses, so credentials, Exchange RBAC scoping and large attachments can be proven before anything touches NAV.
    /// Exit codes: 0 success, 1 failure (the error text is printed exactly as Send/TestConnection returned it, i.e. as
    /// NAV would show it), 2 usage/configuration error or --help.
    /// </summary>
    internal static class Program
    {
        internal const int ExitOk = 0;

        internal const int ExitFailure = 1;

        internal const int ExitUsage = 2;

        private const string Usage = @"nmg - send mail through Microsoft Graph with the NavMailGraph library (the one NAV uses)

Usage:
  nmg token    [auth options]                     acquire a token (GraphMailSender.TestConnection)
  nmg send     [auth options] [message options]   send a mail (GraphMailSender.Send)
  nmg selftest [--verbose]                        offline end-to-end test against an in-process fake Graph server
  nmg version                                     print CLI and library versions
  nmg --help | -h                                 this text

Auth / transport options:
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
  --timeout SECONDS           per HTTP request (default 100)
  --retries N                 retries on HTTP 429/503/509 (default 3)
  --send-x5c                  include the x5c claim in the certificate client assertion
  --log PATH                  append a redacted diagnostic log to PATH
  --verbose                   always print the diagnostic log (it is printed on errors anyway)
  --config PATH               JSON file with the same keys as the long options without '--'
                              (default ./nmg.local.json if present; command-line values override the file)

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

Exit codes: 0 success, 1 failure (error text as NAV shows it), 2 usage error or --help.
Example:
  NMG_SECRET=... nmg send --tenant contoso.onmicrosoft.com --client 1111... --secret-env NMG_SECRET \
      --from nav@contoso.com --to me@contoso.com --subject Test --body Hello --attach ./big.pdf
";

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("nmg: " + ex.Message);
                Console.Error.WriteLine();
                Console.Error.Write(Usage);
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
                Console.Out.Write(Usage);
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
                    return RunToken(CliOptions.Parse(command, rest, false));
                case "send":
                    return RunSend(CliOptions.Parse(command, rest, true));
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

        private static int RunToken(CliOptions options)
        {
            GraphMailSender sender = CreateSender(options);
            string error = sender.TestConnection();
            return Report(sender, options, error, "Token acquired.");
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

            GraphMailSender sender = CreateSender(options);
            sender.SenderMailbox = options.Get("mailbox");
            sender.SaveToSentItems = !options.Flag("no-save-sent");
            sender.ReplyToFromAddress = !options.Flag("no-reply-to-from");
            sender.UseDraftForLargeMessages = !options.Flag("no-draft");

            GraphMailMessage message = BuildMessage(options);
            string error = sender.Send(message);
            string success = "Mail sent: " + message.Describe();
            if (error.Length == 0 && sender.LastMessageId.Length > 0)
            {
                success += Environment.NewLine + "Draft flow used, message id: " + sender.LastMessageId;
            }

            return Report(sender, options, error, success);
        }

        private static GraphMailMessage BuildMessage(CliOptions options)
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
            string tenant = options.Require("tenant").Trim();
            string client = options.Require("client").Trim();
            var sender = new GraphMailSender(tenant, client);

            ConfigureCredential(sender, options);
            sender.TokenLibrary = MapChoice(options, "token-library", new Dictionary<string, string> { { "builtin", "BuiltIn" }, { "msal", "Msal" } }, "BuiltIn");
            sender.Cloud = MapChoice(options, "cloud", new Dictionary<string, string> { { "global", "Global" }, { "usgov", "USGov" }, { "usgovdod", "USGovDoD" }, { "china", "China" } }, "Global");
            if (options.Has("graph-url"))
            {
                sender.GraphBaseUrl = options.Get("graph-url");
            }

            if (options.Has("authority"))
            {
                sender.AuthorityHost = options.Get("authority");
            }

            sender.TimeoutSeconds = options.GetInt("timeout", sender.TimeoutSeconds);
            sender.MaxRetries = options.GetInt("retries", sender.MaxRetries);
            sender.SendX5C = options.Flag("send-x5c");
            if (options.Has("log"))
            {
                sender.LogFilePath = options.Get("log");
            }

            return sender;
        }

        private static void ConfigureCredential(GraphMailSender sender, CliOptions options)
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
                    sender.UseClientSecret(FromEnvironment(options.Get("secret-env"), "secret-env"));
                    break;
                case "secret":
                    WarnCommandLineSecret(options, "secret", "--secret-env NAME");
                    sender.UseClientSecret(options.Get("secret"));
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

                    sender.UseCertificateFile(options.Get("cert-pfx"), password);
                    break;
                default:
                    sender.UseCertificateThumbprint(options.Get("cert-thumbprint"));
                    break;
            }
        }

        private static void WarnCommandLineSecret(CliOptions options, string name, string alternative)
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

        private static string FromEnvironment(string variable, string option)
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            if (value.Length == 0)
            {
                throw new UsageException("--" + option + ": environment variable '" + variable + "' is not set or empty.");
            }

            return value;
        }

        private static string MapChoice(CliOptions options, string name, Dictionary<string, string> choices, string fallback)
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

        private static int Report(GraphMailSender sender, CliOptions options, string error, string success)
        {
            bool failed = error.Length > 0;
            if (options.Flag("verbose") || failed)
            {
                PrintDiagnostics(sender.LastDiagnostics);
            }

            if (failed)
            {
                Console.Error.WriteLine(error);
                return ExitFailure;
            }

            Console.Out.WriteLine(success);
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
