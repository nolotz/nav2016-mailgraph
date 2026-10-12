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

    internal sealed class OptionSpec
    {
        internal OptionSpec(string name, OptionKind kind, bool sendOnly)
        {
            Name = name;
            Kind = kind;
            SendOnly = sendOnly;
        }

        internal string Name { get; }

        internal OptionKind Kind { get; }

        /// <summary>Only meaningful for 'nmg send' (rejected on the command line of other commands, ignored in the config file).</summary>
        internal bool SendOnly { get; }
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

        private static readonly OptionSpec[] Specs =
        {
            // auth / transport
            new OptionSpec("tenant", OptionKind.Value, false),
            new OptionSpec("client", OptionKind.Value, false),
            new OptionSpec("secret-env", OptionKind.Value, false),
            new OptionSpec("secret", OptionKind.Value, false),
            new OptionSpec("cert-pfx", OptionKind.Value, false),
            new OptionSpec("cert-password-env", OptionKind.Value, false),
            new OptionSpec("cert-password", OptionKind.Value, false),
            new OptionSpec("cert-thumbprint", OptionKind.Value, false),
            new OptionSpec("token-library", OptionKind.Value, false),
            new OptionSpec("cloud", OptionKind.Value, false),
            new OptionSpec("graph-url", OptionKind.Value, false),
            new OptionSpec("authority", OptionKind.Value, false),
            new OptionSpec("timeout", OptionKind.Value, false),
            new OptionSpec("retries", OptionKind.Value, false),
            new OptionSpec("send-x5c", OptionKind.Flag, false),
            new OptionSpec("log", OptionKind.Value, false),
            new OptionSpec("verbose", OptionKind.Flag, false),

            // message (send only)
            new OptionSpec("from", OptionKind.Value, true),
            new OptionSpec("to", OptionKind.Repeat, true),
            new OptionSpec("cc", OptionKind.Repeat, true),
            new OptionSpec("bcc", OptionKind.Repeat, true),
            new OptionSpec("reply-to", OptionKind.Repeat, true),
            new OptionSpec("subject", OptionKind.Value, true),
            new OptionSpec("body", OptionKind.Value, true),
            new OptionSpec("body-file", OptionKind.Value, true),
            new OptionSpec("html", OptionKind.Flag, true),
            new OptionSpec("attach", OptionKind.Repeat, true),
            new OptionSpec("inline", OptionKind.Repeat, true),
            new OptionSpec("mailbox", OptionKind.Value, true),
            new OptionSpec("no-save-sent", OptionKind.Flag, true),
            new OptionSpec("no-reply-to-from", OptionKind.Flag, true),
            new OptionSpec("importance", OptionKind.Value, true),
            new OptionSpec("header", OptionKind.Repeat, true),
            new OptionSpec("no-draft", OptionKind.Flag, true),
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
        internal static CliOptions Parse(string command, IList<string> args, bool allowSendOptions)
        {
            var cli = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string? configPath = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                {
                    throw new UsageException("unexpected argument '" + arg + "'.");
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

                if (spec.SendOnly && !allowSendOptions)
                {
                    throw new UsageException("option '--" + name + "' is only valid for 'nmg send'.");
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
            string? effectiveConfig = configPath;
            if (effectiveConfig == null && File.Exists(DefaultConfigFile))
            {
                effectiveConfig = DefaultConfigFile;
            }

            if (effectiveConfig != null)
            {
                options.LoadConfig(effectiveConfig, allowSendOptions);
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

        private void LoadConfig(string path, bool allowSendOptions)
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

                    if (spec.SendOnly && !allowSendOptions)
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
