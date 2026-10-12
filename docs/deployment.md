# Deployment on the NAV Server (Windows)

This page installs the add-in on the Dynamics NAV 2016 service tier and on the C/SIDE development machine.
The NAV objects themselves are covered in [../nav/README.md](../nav/README.md).

## Prerequisites

| Item | Requirement | Check |
|------|-------------|-------|
| OS | Windows Server supported by NAV 2016 (TLS 1.2 is enabled by default on 2012 R2 and later) | – |
| .NET Framework | **4.6.2 or later** (registry `Release` ≥ 394802); 4.8 recommended | `Install-NavMailGraphAddin.ps1` checks it |
| TLS 1.2 | to `login.microsoftonline.com` and `graph.microsoft.com` (Entra ID refuses TLS 1.0/1.1) | see [TLS](#tls-12) |
| Outbound HTTPS | `login.microsoftonline.com`, `graph.microsoft.com`, and `outlook.office.com` (upload sessions for large attachments); other hosts for national clouds | – |
| Certificate | `.pfx` from `New-NavMailGraphCertificate.ps1` or your PKI ([azure-setup.md](azure-setup.md)) | – |
| Payload | `dist/Add-ins/NavMailGraph/` from `./build.sh` or from the release zip | `SHA256SUMS` |

National clouds use other hosts (US Gov: `login.microsoftonline.us`, `graph.microsoft.us` / `dod-graph.microsoft.us`;
China: `login.chinacloudapi.cn`, `microsoftgraph.chinacloudapi.cn`).

## Folder layout

```
C:\Program Files\Microsoft Dynamics NAV\90\Service\Add-ins\NavMailGraph\
    NavMailGraph.dll                         core add-in (always; the only file needed)
    NavMailGraph.xml                         API documentation (optional)
    Msal\                                    only for "Graph Token Library = MSAL"
        NavMailGraph.Msal.dll
        NavMailGraph.Msal.dll.config         generated binding redirects (reference for the server config)
        Microsoft.Identity.Client.dll + dependency DLLs

C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client\Add-ins\NavMailGraph\
    NavMailGraph.dll                         C/SIDE machine only: needed to compile Codeunit 50400

%ProgramData%\NavMailGraph\Backup\           backups made by the install script (outside Add-ins on purpose)
```

* The NAV Server scans the `Add-ins` folder including subfolders. Never keep old copies anywhere below `Add-ins`;
  NAV might load the old one.
* C/AL binds to `NavMailGraph, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`. The AssemblyVersion stays
  `1.0.0.0` in every release, so updates need no C/AL change. The release number is in the file version.
* The DLL is AnyCPU, so the 32-bit C/SIDE and the 64-bit service tier can both load it.

## With the install script

`deploy/Install-NavMailGraphAddin.ps1` (Windows PowerShell 5.1 or PowerShell 7 on Windows, **elevated**).

The release zip contains `Add-ins/`, `nav/` and `SHA256SUMS`, not the `deploy/` scripts. Copy
`Install-NavMailGraphAddin.ps1` from the repository into the extracted release folder (next to `Add-ins`), or pass
`-SourcePath`. In a repository checkout after `./build.sh`, the script finds `..\dist\Add-ins\NavMailGraph` itself.

```powershell
# dry run
.\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls -WhatIf
# real run
.\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls
```

What it does, in order:

1. Finds the payload and verifies it against `SHA256SUMS` (skip with `-SkipHashCheck`).
2. Checks .NET Framework ≥ 4.6.2 and reports the TLS registry values.
3. With `-ServerInstance`: stops `MicrosoftDynamicsNavServer$<instance>` and starts it again at the end, also
   after an error (`-NoRestart` suppresses this).
4. Backs up an existing `Add-ins\NavMailGraph` to `-BackupPath` (default `%ProgramData%\NavMailGraph\Backup`) and
   copies the payload. The `Msal` subfolder only with `-IncludeMsal`.
5. With `-RoleTailoredClient`: the same for the RoleTailored Client Add-ins folder (C/SIDE machine).
6. With `-FixTls`: sets `SchUseStrongCrypto = 1` and `SystemDefaultTlsVersions = 1`.
7. With `-PfxPath`: imports the certificate into `Cert:\LocalMachine\My` (private key non-exportable) and grants
   **Read** on the private key file to the NAV service account (taken from the service, or `-ServiceAccount`).
   Handles CNG and legacy CAPI keys. The PFX password is prompted (or `-PfxPassword` as SecureString).
8. Prints a summary and the values for NAV Page 409.

All parameters: `-SourcePath`, `-NavServicePath` (default `C:\Program Files\Microsoft Dynamics NAV\90\Service`),
`-IncludeMsal`, `-RoleTailoredClient`, `-RoleTailoredClientPath` (default
`C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client`), `-PfxPath`, `-PfxPassword`,
`-ServerInstance`, `-ServiceAccount`, `-NoRestart`, `-FixTls`, `-BackupPath`, `-SkipHashCheck`, plus `-WhatIf` /
`-Confirm`. Full help: `Get-Help .\Install-NavMailGraphAddin.ps1 -Full`.

C/SIDE machine (no service tier there):

```powershell
.\Install-NavMailGraphAddin.ps1 -SourcePath D:\NavMailGraph-0.1.0\Add-ins\NavMailGraph -RoleTailoredClient -NoRestart
```

## Manual installation

1. **Stop** the NAV Server instance (NAV keeps loaded add-in DLLs locked until the service stops).
2. Move an existing `Add-ins\NavMailGraph` folder to a backup location **outside** `Add-ins`.
3. Copy `NavMailGraph.dll` to `C:\Program Files\Microsoft Dynamics NAV\90\Service\Add-ins\NavMailGraph\`.
   Check the file hash against `SHA256SUMS` (`Get-FileHash -Algorithm SHA256`).
4. Optional MSAL: copy the `Msal` subfolder too ([MSAL flavour](#msal-flavour)).
5. Import the certificate and set the private key ACL ([below](#certificate)).
6. Set the TLS registry values if needed ([below](#tls-12)).
7. **Start** the NAV Server instance.
8. On the C/SIDE machine: copy `NavMailGraph.dll` to
   `C:\Program Files (x86)\Microsoft Dynamics NAV\90\RoleTailored Client\Add-ins\NavMailGraph\` and restart C/SIDE.

NAV 2016 can also deploy add-ins from the database (table "Add-ins"). For .NET interop assemblies this is
reported as unreliable in NAV 2016; use the file system.

## Certificate

Import into the machine store (elevated):

```powershell
$pw = Read-Host -AsSecureString 'PFX password'
$cert = Import-PfxCertificate -FilePath C:\Temp\NavMailGraph.pfx -CertStoreLocation Cert:\LocalMachine\My -Password $pw
$cert.Thumbprint     # -> NAV field "Graph Cert. Thumbprint"
```

Without `-Exportable` the private key cannot be exported again. Keep the `.pfx` in a vault for renewal and delete
the copy on the server.

Grant the NAV **service account** access to the private key:

1. `certlm.msc` → Personal → Certificates → the certificate → All Tasks → **Manage Private Keys**.
2. Add the service account (e.g. `NT AUTHORITY\NETWORK SERVICE` or `CONTOSO\svc-nav`), allow **Read**.

Read is normally enough to sign. Microsoft's Business Central guide grants Full Control; if the token request fails
with "Keyset does not exist" or an access-denied error on the key, grant Full Control.

The add-in looks for the thumbprint in `LocalMachine\My`, then `CurrentUser\My` (of the service account).
Spaces and invisible characters copied from the certificate dialog are stripped.

## TLS 1.2

Entra ID accepts only TLS 1.2 (and later). TLS 1.2 is enabled in Schannel by default on Windows Server 2012 R2 and
later. On .NET Framework the add-in additionally enables TLS 1.2 for its own connections at start-up
(`ServicePointManager.SecurityProtocol |= Tls12`).

Microsoft's recommendation for .NET Framework apps is to set these registry values (DWORD), which the install
script sets with `-FixTls`:

```
HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319
    SchUseStrongCrypto       = 1
    SystemDefaultTlsVersions = 1
HKLM\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319
    SchUseStrongCrypto       = 1
    SystemDefaultTlsVersions = 1
```

Processes read them at start-up: restart the NAV Server instance (or reboot) afterwards. These settings affect all
.NET Framework applications on the machine.

## Proxy

The add-in uses the default .NET Framework proxy handling (`HttpClientHandler`, `UseProxy = true`, no explicit
proxy). On .NET Framework that means: the `<system.net><defaultProxy>` setting of the host process config if
present, otherwise the Internet Options (WinINET) proxy of the service account, which is often empty.

If the server needs a proxy, add it to `Microsoft.Dynamics.Nav.Server.exe.config` (in the Service folder;
back it up first) inside `<configuration>`:

```xml
<system.net>
  <defaultProxy enabled="true" useDefaultCredentials="true">
    <proxy proxyaddress="http://proxy.contoso.local:8080" bypassonlocal="true" />
  </defaultProxy>
</system.net>
```

* `useDefaultCredentials="true"` lets the service account authenticate to the proxy (fixes HTTP 407).
* The setting applies to the whole NAV Server process.
* Restart the instance afterwards.
* The proxy must allow `login.microsoftonline.com`, `graph.microsoft.com` and `outlook.office.com` and must not
  break TLS with an untrusted certificate. The add-in never disables certificate validation.

If the config file already has a `<system.net>` element, add `<defaultProxy>` inside it instead of creating a
second one.

## MSAL flavour

Default is the built-in token code in `NavMailGraph.dll` (no extra files). The optional MSAL flavour uses
Microsoft.Identity.Client (MSAL.NET 4.90.1) instead. Use it only if you have a reason (e.g. a policy that requires
Microsoft's auth library).

1. Install with `-IncludeMsal` (or copy the `Msal` subfolder manually).
2. Set NAV *Graph Token Library* = `MSAL`.
3. Restart the instance, run **Test Graph Connection**.

The core loads `Msal\NavMailGraph.Msal.dll` by reflection; NAV does not need it to compile.

MSAL brings 13 strong-named DLLs (MSAL plus dependencies). The .NET Framework loader requires exact versions;
a class library cannot apply binding redirects for its host. If the MSAL flavour fails with
`FileLoadException` / "Could not load file or assembly ... The located assembly's manifest definition does not
match the assembly reference", merge binding redirects into `Microsoft.Dynamics.Nav.Server.exe.config`.

The build generates `Msal\NavMailGraph.Msal.dll.config` with the redirects the compiler found necessary (in the
research build: `System.Memory` → 4.0.1.2 and `System.Runtime.CompilerServices.Unsafe` → 6.0.0.0). Use that file as
the primary source. The full set of strong-named assemblies of the MSAL 4.90.1 closure on net462:

| Assembly | Version | PublicKeyToken |
|----------|---------|----------------|
| Microsoft.Identity.Client | 4.90.1.0 | 0a613f4dd989e8ae |
| Microsoft.IdentityModel.Abstractions | 8.14.0.0 | 31bf3856ad364e35 |
| System.Text.Json | 6.0.0.10 | cc7b13ffcd2ddd51 |
| System.Diagnostics.DiagnosticSource | 6.0.0.1 | cc7b13ffcd2ddd51 |
| System.Formats.Asn1 | 8.0.0.1 | cc7b13ffcd2ddd51 |
| System.Memory | 4.0.1.2 | cc7b13ffcd2ddd51 |
| System.Buffers | 4.0.3.0 | cc7b13ffcd2ddd51 |
| System.Text.Encodings.Web | 6.0.0.0 | cc7b13ffcd2ddd51 |
| System.Threading.Tasks.Extensions | 4.2.0.1 | cc7b13ffcd2ddd51 |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0.0 | cc7b13ffcd2ddd51 |
| System.ValueTuple | 4.0.3.0 | cc7b13ffcd2ddd51 |
| System.Numerics.Vectors | 4.1.4.0 | b03f5f7f11d50a3a |
| System.Runtime.CompilerServices.Unsafe | 6.0.0.0 | b03f5f7f11d50a3a |

Example entry (inside `<configuration>`; merge into an existing `<runtime>` / `<assemblyBinding>` if present):

```xml
<runtime>
  <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
    <dependentAssembly>
      <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
      <bindingRedirect oldVersion="0.0.0.0-4.0.1.2" newVersion="4.0.1.2" />
    </dependentAssembly>
    <dependentAssembly>
      <assemblyIdentity name="System.Runtime.CompilerServices.Unsafe" publicKeyToken="b03f5f7f11d50a3a" culture="neutral" />
      <bindingRedirect oldVersion="0.0.0.0-6.0.0.0" newVersion="6.0.0.0" />
    </dependentAssembly>
  </assemblyBinding>
</runtime>
```

Add an entry for another assembly from the table only when the error message names it. Not verified on a real
NAV 2016 server: whether the NAV Server honours an add-in's `.dll.config` (Microsoft says plugins "might not"),
and which versions of these assemblies the customer's NAV Service folder already contains. NAV 2016 already ships
Newtonsoft.Json 6.0.0.0 and ADAL 2.x; NavMailGraph depends on neither.

## Several instances, several tenants

* **Several NAV Server instances on one machine** share the Service folder and therefore the Add-ins folder.
  Stop **all** instances before replacing the DLL, then start them all. Each instance's service account needs
  Read on the certificate's private key; run the install script once per instance with `-ServerInstance`
  (or `-ServiceAccount`), or set the ACL manually for each account.
* **Several companies / databases** each read their own Table 409 record, so they can use different apps,
  certificates and sender mailboxes. The token cache is process-wide and keyed by authority, tenant, client ID,
  credential and scope, so different apps do not share tokens.
* **NAV multitenancy**: each tenant database has its own setup. Not verified: whether the NAV 2016 service tier
  isolates tenants in separate AppDomains. Functionally this does not matter (the cache key separates apps); it
  only affects how often tokens are requested.
* **Several Microsoft 365 tenants**: one app registration per Microsoft 365 tenant (the apps are single-tenant).

## Update

1. Read the release notes / `CHANGELOG.md`.
2. Run `Install-NavMailGraphAddin.ps1` with the new payload and `-ServerInstance` (backup + restart included).
   Without the script: stop the instance(s), move the old folder to a backup outside `Add-ins`, copy, start.
3. On the C/SIDE machine update the RoleTailored Client Add-ins copy too (needed only if you recompile).
4. Run **Test Graph Connection** and **Send Graph Test Email**.

New C/AL objects or patches are only needed when the release notes say so. If a new Codeunit 50400 uses new add-in
members, install the new DLL **before** importing and compiling the codeunit.

## Rollback

* **Functional, immediate:** NAV Page 409 → *Mail Transport* = `SMTP`. Codeunit 400 runs its original code. No
  restart.
* **Add-in version:** stop the instance, replace `Add-ins\NavMailGraph` with the backup from
  `%ProgramData%\NavMailGraph\Backup\...`, start the instance.
* **Complete removal:** see [../nav/README.md](../nav/README.md#rollback); then delete `Add-ins\NavMailGraph`,
  remove the certificate from `LocalMachine\My`, remove the Exchange scope
  ([exchange-rbac.md](exchange-rbac.md#change-list-remove)) and the app registration.
