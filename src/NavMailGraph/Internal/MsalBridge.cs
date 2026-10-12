using System;
using System.IO;
using System.Reflection;

namespace NavMailGraph.Internal
{
    /// <summary>
    /// Creates <c>NavMailGraph.Msal.MsalTokenProvider</c> by reflection so the core assembly (and therefore the
    /// C/AL objects) compiles and loads without MSAL. Looks for the constructor
    /// <c>MsalTokenProvider(TokenProviderSettings settings)</c>.
    /// </summary>
    internal static class MsalBridge
    {
        internal const string AssemblyName = "NavMailGraph.Msal";

        internal const string TypeName = "NavMailGraph.Msal.MsalTokenProvider";

        internal const string MsalSubfolder = "Msal";

        internal static ITokenProvider Create(TokenProviderSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            Type type = LoadProviderType();
            ConstructorInfo? ctor = type.GetConstructor(new[] { typeof(TokenProviderSettings) });
            if (ctor == null)
            {
                throw GraphMailException.Validation(
                    "Token library 'Msal': " + TypeName + " has no constructor taking NavMailGraph.TokenProviderSettings. "
                    + "NavMailGraph.Msal.dll does not match NavMailGraph.dll " + GraphMailSender.Version + "; deploy matching versions.");
            }

            object instance;
            try
            {
                instance = ctor.Invoke(new object[] { settings });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                GraphMailException? inner = ex.InnerException as GraphMailException;
                if (inner != null)
                {
                    throw inner;
                }

                throw GraphMailException.Validation(
                    "Token library 'Msal': the MSAL token provider could not be created: " + ex.InnerException.Message, ex.InnerException);
            }

            ITokenProvider? provider = instance as ITokenProvider;
            if (provider == null)
            {
                throw GraphMailException.Validation(
                    "Token library 'Msal': " + TypeName + " does not implement NavMailGraph.ITokenProvider "
                    + "(NavMailGraph.Msal.dll was built against a different NavMailGraph.dll).");
            }

            return provider;
        }

        private static Type LoadProviderType()
        {
            try
            {
                // 1. Normal probing (NAV's add-in resolver scans the Add-ins folder recursively).
                Type? type = Type.GetType(TypeName + ", " + AssemblyName, false);
                if (type != null)
                {
                    return type;
                }

                // 2. Next to NavMailGraph.dll or in its "Msal" subfolder (deployment layout from docs/DESIGN.md section 7).
                string? directory = SafeDirectoryOfCore();
                if (!string.IsNullOrEmpty(directory))
                {
                    string[] candidates =
                    {
                        Path.Combine(directory!, AssemblyName + ".dll"),
                        Path.Combine(directory!, MsalSubfolder, AssemblyName + ".dll"),
                    };
                    foreach (string candidate in candidates)
                    {
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        Assembly assembly = Assembly.LoadFrom(candidate);
                        Type? loaded = assembly.GetType(TypeName, false);
                        if (loaded != null)
                        {
                            return loaded;
                        }
                    }
                }
            }
            catch (FileLoadException ex)
            {
                throw GraphMailException.Validation(
                    "Token library 'Msal': NavMailGraph.Msal.dll or one of its dependencies could not be loaded (" + ex.Message + "). "
                    + "Merge the binding redirects from NavMailGraph.Msal.dll.config into Microsoft.Dynamics.Nav.Server.exe.config "
                    + "or set Token Library to BuiltIn.", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw GraphMailException.Validation("Token library 'Msal': NavMailGraph.Msal.dll is not a valid .NET assembly for this process (" + ex.Message + ").", ex);
            }
            catch (Exception ex) when (!(ex is GraphMailException))
            {
                throw GraphMailException.Validation("Token library 'Msal': loading NavMailGraph.Msal.dll failed (" + ex.GetType().Name + ": " + ex.Message + ").", ex);
            }

            throw GraphMailException.Validation(
                "Token library 'Msal' requested but NavMailGraph.Msal.dll was not found (type " + TypeName + "). "
                + "Deploy NavMailGraph.Msal.dll with its dependencies next to NavMailGraph.dll or in its '" + MsalSubfolder
                + "' subfolder, or set Token Library to BuiltIn.");
        }

        private static string? SafeDirectoryOfCore()
        {
            try
            {
                string location = typeof(MsalBridge).Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
