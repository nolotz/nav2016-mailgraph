# Development on macOS

The add-in is built and unit-tested on macOS (Linux works the same). Only two things need Windows: running the
tests on the real .NET Framework runtime (done by CI) and compiling the C/AL objects (C/SIDE).

## Prerequisites

```bash
brew install --cask dotnet-sdk@8   # .NET SDK 8 incl. the .NET 8 runtime (tools/tests target net8.0)
brew install --cask powershell     # pwsh, for the deploy scripts and Exchange/Graph modules
```

* No Mono, no Windows, no Visual Studio. The `net462` target compiles against the reference assemblies from the
  NuGet package `Microsoft.NETFramework.ReferenceAssemblies`, which the SDK restores automatically.
* ExchangeOnlineManagement 3.10.x needs PowerShell 7.6+ (macOS 14 or later). On macOS 13 use module 3.9.2.
* The plain `dotnet-sdk` cask installs only the latest SDK and runtime (10.x). That SDK builds everything, but
  `dotnet test -f net8.0` and `dotnet run --project src/NavMailGraph.Cli` then fail: the net8.0 test host and
  `nmg` need the Microsoft.NETCore.App **8.0** runtime (the default roll-forward policy does not move to a new
  major version). Install `dotnet-sdk@8` next to it, or the .NET 8 runtime. `build.sh` checks this up front.
* .NET 8 reaches end of support on 2026-11-10. The tools target framework is set in one place
  (`ToolsTargetFramework` in `Directory.Build.props`); CI already installs SDK 8 and 10.

## Repository layout

| Path | What |
|------|------|
| `src/NavMailGraph/` | core add-in, `net462;netstandard2.0`, no NuGet dependencies |
| `src/NavMailGraph.Msal/` | optional MSAL token provider (`Microsoft.Identity.Client` 4.90.1) |
| `src/NavMailGraph.Cli/` | `nmg` console tool (net8.0) |
| `tests/NavMailGraph.Tests/` | xunit tests: net8.0 everywhere, additionally net462 on Windows |
| `nav/` | C/AL objects and patches ([../nav/README.md](../nav/README.md)) |
| `deploy/` | PowerShell scripts ([../deploy/README.md](../deploy/README.md)) |
| `Directory.Build.props` | version, AssemblyVersion pin, target frameworks |

## Build

```bash
./build.sh              # restore, build, tests (net8.0), dist/
./build.sh --no-test    # skip tests
./build.sh --clean      # delete bin/obj/dist first
```

Output:

```
dist/Add-ins/NavMailGraph/NavMailGraph.dll        net462 core add-in
dist/Add-ins/NavMailGraph/Msal/...                net462 MSAL flavour (without a second copy of the core DLL)
dist/nav/...                                      copy of nav/
dist/SHA256SUMS                                   checksums of all files above
```

On Windows (Git Bash) `build.sh` also runs the tests on net462.

Rules for the core assembly (see [../CONTRIBUTING.md](../CONTRIBUTING.md) and [DESIGN.md](DESIGN.md) section 4):
no NuGet dependencies, C/AL-friendly public API (no generics, no async, no optional parameters, no overloads with
the same parameter count), `AssemblyVersion` stays `1.0.0.0`.

## Tests

```bash
dotnet test tests/NavMailGraph.Tests/NavMailGraph.Tests.csproj -f net8.0
```

The unit tests use a fake HTTP handler; they need no tenant and no network.

## End-to-end test with `nmg`

`nmg` sends real mail through Microsoft Graph with the same library NAV uses. Use a **test tenant** (or at least a
test app and test mailbox) first.

### Setup

1. Create a test app and the RBAC scope as in [azure-setup.md](azure-setup.md) and
   [exchange-rbac.md](exchange-rbac.md). A short-lived client secret is fine for a test tenant; a certificate
   works too (`--cert-pfx`).
2. Copy the example config and fill in your values:

   ```bash
   cp src/NavMailGraph.Cli/nmg.example.json nmg.local.json     # nmg.local.json is git-ignored
   ```

   ```json
   {
     "tenant": "contoso.onmicrosoft.com",
     "client": "00000000-0000-0000-0000-000000000000",
     "secret-env": "NMG_CLIENT_SECRET",
     "from": "NAV <nav@contoso.com>",
     "to": ["you@contoso.com"],
     "subject": "nmg test mail",
     "body": "Hello from nmg."
   }
   ```

   Keys are the long option names without `--`. `nmg` reads `./nmg.local.json` from the current directory when
   it exists, or the file given with `--config PATH`. Command-line options override file values. **Never put a
   secret into the file**: use `secret-env` (name of an environment variable) or `cert-password-env`.

### Run

```bash
export NMG_CLIENT_SECRET='...'          # from the app registration (the Value, not the Secret ID)
alias nmg='dotnet run --project src/NavMailGraph.Cli --'

nmg --help                               # all options
nmg version
nmg selftest --verbose                   # offline: full flow against an in-process fake Graph server
nmg token                                # acquire a token only (like "Test Graph Connection")
nmg send                                 # send with the values from nmg.local.json
nmg send --to me@contoso.com --subject "Large" --body "see attachment" --attach ./big.pdf   # attachments > ~2.5 MB: draft flow
nmg send --mailbox nav@contoso.com --from "Jane <jane@contoso.com>" --to me@contoso.com --subject "Reply-To test" --body x
```

Main options (`nmg --help` is authoritative):

| Group | Options |
|-------|---------|
| Auth | `--tenant`, `--client`, one of `--secret-env` / `--secret` / `--cert-pfx` (+ `--cert-password-env` / `--cert-password`) / `--cert-thumbprint` |
| Transport | `--token-library builtin\|msal`, `--cloud global\|usgov\|usgovdod\|china`, `--graph-url`, `--authority`, `--timeout`, `--retries`, `--send-x5c` |
| Diagnostics | `--log PATH` (redacted log file), `--verbose` (always print the diagnostic log) |
| Message | `--from`, `--mailbox`, `--no-reply-to-from`, `--to`, `--cc`, `--bcc`, `--reply-to`, `--subject`, `--body` / `--body-file`, `--html`, `--attach`, `--inline PATH:CID`, `--importance`, `--header "x-name: value"`, `--no-save-sent`, `--no-draft` |

Exit codes: `0` success, `1` failure (the error text exactly as NAV would show it, followed by the diagnostic
log), `2` usage error or `--help`.

What to test before touching NAV:

* `nmg token` succeeds (credentials, tenant, TLS, proxy).
* `nmg send` to an internal and an external recipient.
* A message with an attachment larger than 4 MB (draft + upload session; needs `Application Mail.ReadWrite`).
  With `--no-draft` it must fail with a clear "too large" error.
* `--mailbox` with a mailbox **outside** the RBAC scope must fail with HTTP 403 (after the 30 min – 2 h cache
  window).
* `--cert-thumbprint` only works where a certificate store with the key exists (Windows); on macOS use `--cert-pfx`.

## What cannot be tested on macOS

| What | Where it is tested |
|------|--------------------|
| net462 build on the real .NET Framework runtime (DataContractJsonSerializer, `HttpClient` on .NET Framework, Windows crypto / CAPI keys) | GitHub Actions `windows-latest`: `dotnet test -f net462` |
| Certificate from `LocalMachine\My`, private key ACL | a Windows test server |
| C/AL objects (Codeunit 50400, patches to Codeunit 400, Table 409, Page 409) | C/SIDE in a NAV 2016 development database; **not in CI** |
| Loading inside the NAV 2016 service tier, MSAL binding redirects | a NAV 2016 test server |

## CI

`.github/workflows/ci.yml` runs on push to `main` and on pull requests:

* matrix `ubuntu-latest`, `macos-latest`, `windows-latest`: restore, build (net462 + netstandard2.0 + tools),
  tests on net8.0;
* Windows only: tests on net462 and `./build.sh --no-test`; uploads `dist/` as artifact `NavMailGraph-Addin`;
* PSScriptAnalyzer over `deploy/` (errors fail the job).

## Release process

1. Bump `VersionPrefix` in `Directory.Build.props` (e.g. `0.2.0`). Do **not** change `AssemblyVersion`
   (`1.0.0.0`; C/AL binds to it).
2. Move the *Unreleased* entries in `CHANGELOG.md` under the new version.
3. Commit, then tag and push:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

4. `.github/workflows/release.yml` (on tags `v*`, `windows-latest`) checks that the tag equals `VersionPrefix`,
   runs `./build.sh` (tests on net8.0 and net462), zips `Add-ins`, `nav` and `SHA256SUMS` into
   `NavMailGraph-<version>.zip`, writes `NavMailGraph-<version>.zip.sha256` and publishes a GitHub release with
   generated notes.
5. Check the release assets. The C/AL objects in the zip are still uncompiled; test them in C/SIDE.
