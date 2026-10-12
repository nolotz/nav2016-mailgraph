# Contributing

Thanks for helping. A few ground rules keep this add-in safe to drop into a NAV server.

## Ground rules

* `src/NavMailGraph` must stay **free of NuGet dependencies**. Anything that needs a package goes into a
  separate assembly (like `NavMailGraph.Msal`). Reason: every extra DLL in the NAV `Add-ins` folder is a
  potential version conflict on a customer server.
* `AssemblyVersion` stays `1.0.0.0`. C/AL DotNet variables bind to it. Bump `VersionPrefix` in
  `Directory.Build.props` for releases instead.
* Public API of the core assembly is C/AL-facing: only simple types, no generics, no optional
  parameters, no `async` methods, no overloads with the same number of parameters. Append-only within 1.x.
* No Microsoft C/AL object code in this repository. Changes to Codeunit 400 / Page 409 / Table 409 are
  documented as patches in `nav/`; only objects we wrote ourselves (50xxx range) are shipped as `.TXT`.
* Secrets never reach logs or error messages. Add a test when you touch error formatting.

## Development

```bash
./build.sh                 # build, tests (net8.0), dist/Add-ins
dotnet test -f net8.0      # just the tests
dotnet run --project src/NavMailGraph.Cli -- --help   # end-to-end test against a real tenant
```

On Windows `./build.sh` (Git Bash) or the same `dotnet` commands additionally run the tests on net462.
CI runs ubuntu/macos/windows; the Windows job is the one that proves the .NET Framework build.

## Pull requests

* One topic per PR, with tests.
* Update `CHANGELOG.md` under *Unreleased*.
* If you change the Graph request shapes, link the Microsoft Learn page you verified against.
