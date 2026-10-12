# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- Core add-in `NavMailGraph.dll` (net462, netstandard2.0): Microsoft Graph `sendMail` with application
  permissions, built-in client-credentials flow (client secret or certificate), large attachments via
  draft + upload session, retries, redacted diagnostics.
- Optional `NavMailGraph.Msal.dll` token provider based on Microsoft.Identity.Client.
- `nmg` CLI for end-to-end tests from macOS/Windows.
- NAV 2016 objects: Codeunit 50400 "Graph Mail", Table/Page 409 patches, Codeunit 400 patch.
- PowerShell: Entra app registration, Exchange Online RBAC scoping, NAV add-in installation.
- Documentation in English with a German README and customer-facing permission text.
