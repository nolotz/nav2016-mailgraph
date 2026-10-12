# Security policy

Please report vulnerabilities privately via GitHub "Report a vulnerability" (Security tab) or to the
repository owner. Do not open public issues for security problems.

What we consider in scope: credential handling (client secrets, certificates, tokens), TLS behaviour,
information leakage in logs/error texts, request forgery against Microsoft Graph, and the PowerShell
deployment scripts.

Design notes relevant to security are in `docs/DESIGN.md` (section 10) and `docs/exchange-rbac.md`
(limiting the app to the sender mailbox).
