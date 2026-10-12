using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NavMailGraph.Cli
{
    /// <summary>Thrown for wrong command-line usage or configuration; the CLI prints it with the usage text and exits with 2.</summary>
    internal sealed class UsageException : Exception
    {
        internal UsageException(string message)
            : base(message)
        {
        }
    }

    internal enum OptionKind
    {
        /// <summary>Takes one value; the last one wins.</summary>
        Value,

        /// <summary>Takes one value; may be given several times (all values are kept).</summary>
        Repeat,

        /// <summary>No value; present = true.</summary>
        Flag,
    }

    /// <summary>Which commands an option belongs to.</summary>
    [Flags]
    internal enum OptionScope
    {
        /// <summary>Auth/transport options: valid for every command that talks to a service.</summary>
        Auth = 1,

        /// <summary>Message options of 'nmg send'.</summary>
        Send = 2,

        /// <summary>Options of 'nmg inbox'.</summary>
        Inbox = 4,

        /// <summary>Object storage options ('nmg storage', and 'nmg send' with --link-storage/--attach-storage).</summary>
        Storage = 8,
    }

    internal sealed class OptionSpec
    {
        internal OptionSpec(string name, OptionKind kind, OptionScope scope)
        {
            Name = name;
            Kind = kind;
            Scope = scope;
        }

        internal string Name { get; }

        internal OptionKind Kind { get; }

        /// <summary>Commands the option is valid for (rejected on the command line of other commands, ignored in the config file).</summary>
        internal OptionScope Scope { get; }
    }

    /// <summary>
    /// Parsed options. Keys are option names without the leading "--". Sources, lowest priority first: the JSON config
    /// file (--config PATH, default ./nmg.local.json when present), then the command line. A key given on the command
    /// line replaces the file's value for that key (lists included). Credentials and body sources are treated as groups:
    /// if the command line names any member of a group, all file values of that group are dropped.
    /// </summary>
    internal sealed class CliOptions
    {
        internal const string DefaultConfigFile = "nmg.local.json";

        private const OptionScope All = OptionScope.Auth | OptionScope.Send | OptionScope.Inbox | OptionScope.Storage;

        private static readonly OptionSpec[] Specs =
        {
            // auth / transport (storage commands need them only for --provider azure-entra)
            new OptionSpec("tenant", OptionKind.Value, All),
            new OptionSpec("client", OptionKind.Value, All),
            new OptionSpec("secret-env", OptionKind.Value, All),
            new OptionSpec("secret", OptionKind.Value, All),
            new OptionSpec("cert-pfx", OptionKind.Value, All),
            new OptionSpec("cert-password-env", OptionKind.Value, All),
            new OptionSpec("cert-password", OptionKind.Value, All),
            new OptionSpec("cert-thumbprint", OptionKind.Value, All),
            new OptionSpec("token-library", OptionKind.Value, All),
            new OptionSpec("cloud", OptionKind.Value, All),
            new OptionSpec("graph-url", OptionKind.Value, All),
            new OptionSpec("authority", OptionKind.Value, All),
            new OptionSpec("timeout", OptionKind.Value, All),
            new OptionSpec("retries", OptionKind.Value, All),
            new OptionSpec("send-x5c", OptionKind.Flag, All),
            new OptionSpec("log", OptionKind.Value, All),
            new OptionSpec("verbose", OptionKind.Flag, All),

            // mailbox: sender mailbox for 'send', the mailbox to read for 'inbox'
            new OptionSpec("mailbox", OptionKind.Value, OptionScope.Send | OptionScope.Inbox),

            // message (send only)
            new OptionSpec("from", OptionKind.Value, OptionScope.Send),
            new OptionSpec("to", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("cc", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("bcc", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("reply-to", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("subject", OptionKind.Value, OptionScope.Send),
            new OptionSpec("body", OptionKind.Value, OptionScope.Send),
            new OptionSpec("body-file", OptionKind.Value, OptionScope.Send),
            new OptionSpec("html", OptionKind.Flag, OptionScope.Send),
            new OptionSpec("attach", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("inline", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("no-save-sent", OptionKind.Flag, OptionScope.Send),
            new OptionSpec("no-reply-to-from", OptionKind.Flag, OptionScope.Send),
            new OptionSpec("importance", OptionKind.Value, OptionScope.Send),
            new OptionSpec("header", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("no-draft", OptionKind.Flag, OptionScope.Send),
            new OptionSpec("attach-storage", OptionKind.Repeat, OptionScope.Send),
            new OptionSpec("link-storage", OptionKind.Flag, OptionScope.Send),
            new OptionSpec("link-threshold-mb", OptionKind.Value, OptionScope.Send),
            new OptionSpec("link-days", OptionKind.Value, OptionScope.Send),
            new OptionSpec("link-text", OptionKind.Value, OptionScope.Send),

            // inbox
            new OptionSpec("folder", OptionKind.Value, OptionScope.Inbox),
            new OptionSpec("unread", OptionKind.Flag, OptionScope.Inbox),
            new OptionSpec("since", OptionKind.Value, OptionScope.Inbox),
            new OptionSpec("max", OptionKind.Value, OptionScope.Inbox),
            new OptionSpec("body-text", OptionKind.Flag, OptionScope.Inbox),
            new OptionSpec("full-body", OptionKind.Flag, OptionScope.Inbox),
            new OptionSpec("link-file", OptionKind.Value, OptionScope.Inbox),

            // object storage
            new OptionSpec("provider", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("account", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("account-key-env", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("container", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("sas-url-env", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("endpoint", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("blob-suffix", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("region", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("bucket", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("access-key", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("secret-key-env", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("session-token-env", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("path-style", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("prefix", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("threshold-mb", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("part-mb", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("storage-timeout", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("content-type", OptionKind.Value, OptionScope.Storage),
            new OptionSpec("minutes", OptionKind.Value, OptionScope.Storage),
        };

        private static readonly string[] CredentialGroup = { "secret-env", "secret", "cert-pfx", "cert-password-env", "cert-password", "cert-thumbprint" };

        private static readonly string[] BodyGroup = { "body", "body-file" };

        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        private CliOptions(string command)
        {
            Command = command;
        }

        internal string Command { get; }

        /// <summary>Path of the config file that was loaded, or "" when none.</summary>
        internal string ConfigFile { get; private set; } = "";

        /// <summary>Options that came from the command line (used for warnings about secrets on the command line).</summary>
        internal HashSet<string> FromCommandLine { get; } = new HashSet<string>(StringComparer.Ordinal);

        internal static OptionSpec? FindSpec(string name)
        {
            return Specs.FirstOrDefault(s => s.Name == name);
        }

        /// <summary>Parses the arguments after the command name and merges the config file.</summary>
        /// <param name="command">Command name for messages, e.g. "send" or "inbox list".</param>
        /// <param name="args">Arguments after the command (and subcommand).</param>
        /// <param name="scope">Option groups the command accepts.</param>
        /// <param name="maxPositionals">Number of non-option arguments the command accepts (they end up in <see cref="Positionals"/>).
        /// After "--" every argument is positional.</param>
        internal static CliOptions Parse(string command, IList<string> args, OptionScope scope, int maxPositionals)
        {
            var cli = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var positionals = new List<string>();
            string? configPath = null;
            bool optionsEnded = false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (!optionsEnded && arg == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                if (optionsEnded || !arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (positionals.Count >= maxPositionals)
                    {
                        throw new UsageException("unexpected argument '" + arg + "'" + (maxPositionals == 0 ? "" : " ('nmg " + command + "' takes at most " + maxPositionals + ")") + ".");
                    }

                    positionals.Add(arg);
                    continue;
                }

                string name = arg.Substring(2);
                string? inlineValue = null;
                int eq = name.IndexOf('=');
                if (eq > 0)
                {
                    inlineValue = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }

                if (name == "config")
                {
                    configPath = inlineValue ?? TakeValue(args, ref i, name);
                    continue;
                }

                OptionSpec? spec = FindSpec(name);
                if (spec == null)
                {
                    throw new UsageException("unknown option '--" + name + "'.");
                }

                if ((spec.Scope & scope) == 0)
                {
                    throw new UsageException("option '--" + name + "' is not valid for 'nmg " + command + "' (it belongs to " + DescribeScope(spec.Scope) + ").");
                }

                List<string> list;
                if (!cli.TryGetValue(name, out list!))
                {
                    list = new List<string>();
                    cli[name] = list;
                }

                if (spec.Kind == OptionKind.Flag)
                {
                    if (inlineValue != null)
                    {
                        throw new UsageException("option '--" + name + "' does not take a value.");
                    }

                    list.Clear();
                    list.Add("true");
                    continue;
                }

                string value = inlineValue ?? TakeValue(args, ref i, name);
                if (spec.Kind == OptionKind.Value)
                {
                    list.Clear();
                }

                list.Add(value);
            }

            var options = new CliOptions(command);
            options.Positionals = positionals;
            string? effectiveConfig = configPath;
            if (effectiveConfig == null && File.Exists(DefaultConfigFile))
            {
                effectiveConfig = DefaultConfigFile;
            }

            if (effectiveConfig != null)
            {
                options.LoadConfig(effectiveConfig, scope);
            }

            if (cli.Keys.Any(k => CredentialGroup.Contains(k)))
            {
                foreach (string key in CredentialGroup)
                {
                    options._values.Remove(key);
                }
            }

            if (cli.Keys.Any(k => BodyGroup.Contains(k)))
            {
                foreach (string key in BodyGroup)
                {
                    options._values.Remove(key);
                }
            }

            foreach (KeyValuePair<string, List<string>> pair in cli)
            {
                options._values[pair.Key] = pair.Value;
                options.FromCommandLine.Add(pair.Key);
            }

            return options;
        }

        /// <summary>Non-option arguments in order.</summary>
        internal IReadOnlyList<string> Positionals { get; private set; } = Array.Empty<string>();

        internal bool Has(string name)
        {
            List<string>? list;
            return _values.TryGetValue(name, out list) && list.Count > 0;
        }

        internal bool Flag(string name)
        {
            List<string>? list;
            return _values.TryGetValue(name, out list) && list.Count > 0 && list[list.Count - 1] == "true";
        }

        internal string Get(string name)
        {
            List<string>? list;
            return _values.TryGetValue(name, out list) && list.Count > 0 ? list[list.Count - 1] : "";
        }

        internal IReadOnlyList<string> GetAll(string name)
        {
            List<string>? list;
            return _values.TryGetValue(name, out list) ? list : (IReadOnlyList<string>)Array.Empty<string>();
        }

        internal string Require(string name)
        {
            string value = Get(name);
            if (value.Trim().Length == 0)
            {
                throw new UsageException("missing required option '--" + name + "'.");
            }

            return value;
        }

        internal int GetInt(string name, int fallback)
        {
            string value = Get(name);
            if (value.Length == 0)
            {
                return fallback;
            }

            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < 0)
            {
                throw new UsageException("option '--" + name + "' needs a non-negative integer, got '" + value + "'.");
            }

            return result;
        }

        private static string TakeValue(IList<string> args, ref int i, string name)
        {
            if (i + 1 >= args.Count)
            {
                throw new UsageException("option '--" + name + "' needs a value.");
            }

            i++;
            return args[i];
        }

        /// <summary>Positive number of mebibytes (whole numbers) as bytes; <paramref name="fallback"/> when the option is not given.</summary>
        internal long GetMegabytes(string name, long fallback)
        {
            string value = Get(name);
            if (value.Length == 0)
            {
                return fallback;
            }

            long result;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result <= 0 || result > 1024L * 1024)
            {
                throw new UsageException("option '--" + name + "' needs a whole number of MB between 1 and 1048576, got '" + value + "'.");
            }

            return result * 1024 * 1024;
        }

        private static string DescribeScope(OptionScope scope)
        {
            var names = new List<string>();
            if ((scope & OptionScope.Send) != 0)
            {
                names.Add("'nmg send'");
            }

            if ((scope & OptionScope.Inbox) != 0)
            {
                names.Add("'nmg inbox'");
            }

            if ((scope & OptionScope.Storage) != 0)
            {
                names.Add("'nmg storage'");
            }

            return string.Join(" and ", names);
        }

        private void LoadConfig(string path, OptionScope scope)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new UsageException("cannot read config file '" + path + "': " + ex.Message);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                throw new UsageException("config file '" + path + "' is not valid JSON: " + ex.Message);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new UsageException("config file '" + path + "' must contain a JSON object.");
                }

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    string name = property.Name;
                    if (name.StartsWith("_", StringComparison.Ordinal) || name.StartsWith("$", StringComparison.Ordinal))
                    {
                        continue; // comments / schema hints
                    }

                    OptionSpec? spec = FindSpec(name);
                    if (spec == null)
                    {
                        throw new UsageException("config file '" + path + "': unknown key '" + name + "'.");
                    }

                    if ((spec.Scope & scope) == 0)
                    {
                        continue;
                    }

                    var list = new List<string>();
                    JsonElement value = property.Value;
                    switch (value.ValueKind)
                    {
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            if (spec.Kind != OptionKind.Flag)
                            {
                                throw new UsageException("config file '" + path + "': key '" + name + "' needs a string value.");
                            }

                            if (value.ValueKind == JsonValueKind.True)
                            {
                                list.Add("true");
                            }

                            break;
                        case JsonValueKind.String:
                        case JsonValueKind.Number:
                            if (spec.Kind == OptionKind.Flag)
                            {
                                throw new UsageException("config file '" + path + "': key '" + name + "' needs true or false.");
                            }

                            string scalar = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                            if (scalar.Length > 0)
                            {
                                list.Add(scalar);
                            }

                            break;
                        case JsonValueKind.Array:
                            if (spec.Kind != OptionKind.Repeat)
                            {
                                throw new UsageException("config file '" + path + "': key '" + name + "' does not accept a list.");
                            }

                            foreach (JsonElement item in value.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.String)
                                {
                                    throw new UsageException("config file '" + path + "': key '" + name + "' must be a list of strings.");
                                }

                                list.Add(item.GetString() ?? "");
                            }

                            break;
                        case JsonValueKind.Null:
                            break;
                        default:
                            throw new UsageException("config file '" + path + "': key '" + name + "' has an unsupported value.");
                    }

                    _values[name] = list;
                }
            }

            ConfigFile = path;
        }
    }
}
