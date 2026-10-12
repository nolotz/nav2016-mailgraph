using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NavMailGraph.Cli
{
    /// <summary>
    /// 'nmg storage ...': object storage with <see cref="ObjectStorageClient"/> (docs/DESIGN-v2.md sections 2.3 and 5.3), and the
    /// storage client for 'nmg send --link-storage/--attach-storage'. Secrets are read from environment variables only.
    /// </summary>
    internal static class StorageCommands
    {
        private const int MaxPartMb = 2047;

        private static readonly Dictionary<string, string> Providers = new Dictionary<string, string>
        {
            { "azure-key", "azure-key" },
            { "azure-sas", "azure-sas" },
            { "azure-entra", "azure-entra" },
            { "s3", "s3" },
        };

        internal static int Run(List<string> args)
        {
            if (args.Count == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
            {
                throw new UsageException("missing storage subcommand: test, put, get, ls, rm, url or size.");
            }

            string sub = args[0];
            List<string> rest = args.Skip(1).ToList();
            switch (sub)
            {
                case "test":
                    return Test(Parse(sub, rest, 0, 0, "test"));
                case "put":
                    return Put(Parse(sub, rest, 1, 2, "put FILE [KEY]"));
                case "get":
                    return Get(Parse(sub, rest, 2, 2, "get KEY PATH"));
                case "ls":
                    return Ls(Parse(sub, rest, 0, 1, "ls [PREFIX]"));
                case "rm":
                    return Rm(Parse(sub, rest, 1, 1, "rm KEY"));
                case "url":
                    return Url(Parse(sub, rest, 1, 1, "url KEY [--minutes N]"));
                case "size":
                    return Size(Parse(sub, rest, 1, 1, "size KEY"));
                default:
                    throw new UsageException("unknown storage subcommand '" + sub + "'.");
            }
        }

        private static CliOptions Parse(string sub, List<string> rest, int minPositionals, int maxPositionals, string usage)
        {
            CliOptions options = CliOptions.Parse("storage " + sub, rest, OptionScope.Auth | OptionScope.Storage, maxPositionals);
            if (options.Positionals.Count < minPositionals)
            {
                throw new UsageException("usage: nmg storage " + usage + " [storage options].");
            }

            if (options.FromCommandLine.Contains("content-type") && sub != "put")
            {
                throw new UsageException("option '--content-type' is only valid for 'nmg storage put'.");
            }

            if (options.FromCommandLine.Contains("minutes") && sub != "url")
            {
                throw new UsageException("option '--minutes' is only valid for 'nmg storage url'.");
            }

            return options;
        }

        /// <summary>Creates and configures the storage client from the storage options (throws UsageException for missing options).</summary>
        internal static ObjectStorageClient CreateClient(CliOptions options)
        {
            if (!options.Has("provider"))
            {
                throw new UsageException("missing required option '--provider' (azure-key|azure-sas|azure-entra|s3).");
            }

            string provider = Program.MapChoice(options, "provider", Providers, "");
            var storage = new ObjectStorageClient();

            // Properties that the Use* methods read or keep: set them first.
            if (options.Has("blob-suffix"))
            {
                storage.BlobEndpointSuffix = options.Get("blob-suffix").Trim();
            }

            switch (provider)
            {
                case "azure-key":
                    if (options.Has("endpoint"))
                    {
                        storage.ServiceUrl = options.Get("endpoint").Trim();
                    }

                    storage.UseAzureBlobSharedKey(
                        options.Require("account"),
                        Program.FromEnvironment(options.Require("account-key-env"), "account-key-env"),
                        options.Require("container"));
                    break;
                case "azure-sas":
                    storage.UseAzureBlobSasUrl(Program.FromEnvironment(options.Require("sas-url-env"), "sas-url-env"));
                    break;
                case "azure-entra":
                    if (options.Has("endpoint"))
                    {
                        storage.ServiceUrl = options.Get("endpoint").Trim();
                    }

                    string account = options.Require("account");
                    string container = options.Require("container");
                    storage.UseAzureBlobEntra(Program.CreateConnection(options), account, container);
                    break;
                default:
                    storage.UseS3(
                        options.Get("endpoint"),
                        options.Get("region"),
                        options.Require("bucket"),
                        options.Require("access-key"),
                        Program.FromEnvironment(options.Require("secret-key-env"), "secret-key-env"));
                    if (options.Has("session-token-env"))
                    {
                        storage.S3SessionToken = Program.FromEnvironment(options.Get("session-token-env"), "session-token-env");
                    }

                    switch (options.Get("path-style").Trim().ToLowerInvariant())
                    {
                        case "":
                        case "auto":
                            break; // library default (ObjectStorageClient.S3PathStyle)
                        case "on":
                            storage.S3PathStyle = true;
                            break;
                        case "off":
                            storage.S3PathStyle = false;
                            break;
                        default:
                            throw new UsageException("--path-style must be auto, on or off (got '" + options.Get("path-style") + "').");
                    }

                    break;
            }

            if (options.Has("prefix"))
            {
                storage.KeyPrefix = options.Get("prefix");
            }

            storage.MultipartThresholdBytes = options.GetMegabytes("threshold-mb", storage.MultipartThresholdBytes);
            long part = options.GetMegabytes("part-mb", storage.PartSizeBytes);
            if (part > MaxPartMb * 1024L * 1024)
            {
                throw new UsageException("option '--part-mb' must be at most " + MaxPartMb + ", got '" + options.Get("part-mb") + "'.");
            }

            storage.PartSizeBytes = (int)part;
            int timeout = options.GetInt("storage-timeout", storage.TimeoutSeconds);
            if (timeout < 1)
            {
                throw new UsageException("option '--storage-timeout' must be at least 1 second.");
            }

            storage.TimeoutSeconds = timeout;
            if (options.Has("log"))
            {
                storage.LogFilePath = options.Get("log");
            }

            return storage;
        }

        private static int Test(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string error = storage.TestConnection();
            return Program.Report(storage.LastDiagnostics, options, error, "Storage OK: " + storage.Describe());
        }

        private static int Put(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string file = options.Positionals[0];
            string key = options.Positionals.Count > 1 ? options.Positionals[1] : Path.GetFileName(file);
            string error = storage.UploadFile(file, key, options.Get("content-type"));
            string size = "";
            if (error.Length == 0)
            {
                try
                {
                    size = " (" + new FileInfo(file).Length.ToString(CultureInfo.InvariantCulture) + " bytes)";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            return Program.Report(storage.LastDiagnostics, options, error, "Uploaded " + file + size + " to " + key + ".");
        }

        private static int Get(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string key = options.Positionals[0];
            string path = options.Positionals[1];
            string error = storage.DownloadToFile(key, path);
            string size = "";
            if (error.Length == 0)
            {
                try
                {
                    size = " (" + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) + " bytes)";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            return Program.Report(storage.LastDiagnostics, options, error, "Downloaded " + key + " to " + path + size + ".");
        }

        private static int Ls(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string prefix = options.Positionals.Count > 0 ? options.Positionals[0] : "";
            string error = storage.List(prefix);
            if (error.Length == 0)
            {
                int width = 4;
                for (int i = 0; i < storage.ListCount; i++)
                {
                    width = Math.Max(width, storage.GetListedSize(i).ToString(CultureInfo.InvariantCulture).Length);
                }

                for (int i = 0; i < storage.ListCount; i++)
                {
                    Console.Out.WriteLine(storage.GetListedSize(i).ToString(CultureInfo.InvariantCulture).PadLeft(width) + "  " + storage.GetListedKey(i));
                }
            }

            return Program.Report(storage.LastDiagnostics, options, error, storage.ListCount + " object(s).");
        }

        private static int Rm(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string key = options.Positionals[0];
            string error = storage.Delete(key);
            return Program.Report(storage.LastDiagnostics, options, error, "Deleted " + key + " (or it did not exist).");
        }

        private static int Url(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            int minutes = options.GetInt("minutes", 60);
            if (minutes < 1)
            {
                throw new UsageException("option '--minutes' must be at least 1.");
            }

            string url = storage.GetDownloadUrl(options.Positionals[0], minutes);
            return Program.Report(storage.LastDiagnostics, options, storage.LastError, url);
        }

        private static int Size(CliOptions options)
        {
            ObjectStorageClient storage = CreateClient(options);
            string key = options.Positionals[0];
            long size = storage.GetSize(key);
            if (size < 0 && storage.LastError.Length == 0)
            {
                return Program.Report(storage.LastDiagnostics, options, "Object " + key + " does not exist.", "");
            }

            return Program.Report(storage.LastDiagnostics, options, storage.LastError, size.ToString(CultureInfo.InvariantCulture));
        }
    }
}
