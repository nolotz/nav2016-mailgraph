# nav2016-mailgraph

E-Mails aus **Microsoft Dynamics NAV 2016** über **Microsoft Graph** statt über SMTP versenden.

[English version](README.md)

Ein kleines .NET-Add-In (`NavMailGraph.dll`) und eine neue C/AL-Codeunit ersetzen den SMTP-Versand hinter
Codeunit 400 „SMTP Mail“. Alle bestehenden Aufrufer (Belegversand, Mahnungen, Berichtsversand,
Benachrichtigungs-E-Mails, eigener Code) funktionieren unverändert weiter. Der Versand läuft über
`POST /users/{postfach}/sendMail` mit Anwendungsberechtigungen (OAuth 2.0 Client Credentials).

## Warum

* Codeunit 400 in NAV 2016 kann nur SMTP mit Benutzername und Kennwort. Microsoft hat das Ende der
  Standardauthentifizierung (Basic Authentication) für SMTP AUTH in Exchange Online angekündigt (aktuellen
  Zeitplan bei Microsoft prüfen).
* Graph mit Anwendungsberechtigungen braucht kein Benutzerkennwort und kein SMTP-Relay.
* Mit **RBAC for Applications** in Exchange Online lässt sich die App auf das eine Absenderpostfach von NAV
  beschränken. Andere Postfächer kann sie weder lesen noch daraus senden.

## Architektur

```mermaid
flowchart LR
    subgraph NAV["NAV 2016 Server (Windows, .NET Framework 4.6.2+)"]
        callers["Aufrufer<br/>CU 260 / 9520 / 412 / Berichte / eigener Code"]
        cu400["Codeunit 400 'SMTP Mail'<br/>(gepatcht: delegiert bei<br/>Mailversand = Microsoft Graph)"]
        cu50400["Codeunit 50400 'Graph Mail'"]
        dll["NavMailGraph.dll<br/>(Add-ins-Ordner, keine NuGet-Abhängigkeiten)"]
        t409[("Tabelle 409 'SMTP Mail Setup'<br/>+ Graph-Felder<br/>Tabelle 1261 Service Password")]
        callers --> cu400 --> cu50400 --> dll
        cu50400 -. liest .-> t409
    end
    dll -- "Client Credentials<br/>(Zertifikat oder Secret)" --> entra["Microsoft Entra ID<br/>login.microsoftonline.com"]
    dll -- "sendMail / Entwurf + Upload-Sitzung" --> graph["Microsoft Graph<br/>graph.microsoft.com"]
    graph --> exo["Exchange Online<br/>Absenderpostfach<br/>(RBAC-for-Applications-Bereich)"]
```

Dasselbe als Text:

```
 NAV-Aufrufer ──> Codeunit 400 ──(Mailversand = Microsoft Graph)──> Codeunit 50400 ──> NavMailGraph.dll
                      │                                                                      │
                      └──(Mailversand = SMTP: Originalcode, unverändert)                     │
                                                                                             ▼
                              Entra-ID-Token (Client Credentials) ──> Microsoft Graph ──> Exchange-Online-Postfach
```

## Funktionen

* Direkter Ersatz für Codeunit 400 in NAV 2016: alle Signaturen unverändert, Rückweg durch Umstellen von
  *Mailversand* auf `SMTP`.
* Kern-Add-In **ohne NuGet-Abhängigkeiten**: eine DLL im Add-ins-Ordner, keine Binding Redirects.
* Anmeldung mit **Zertifikat** (empfohlen, `LocalMachine\My` per Fingerabdruck) oder geheimem Clientschlüssel
  (verschlüsselt in NAV-Tabelle 1261 „Service Password“, sofern die Datenbank einen Verschlüsselungsschlüssel hat).
* Optionaler MSAL-Token-Provider (`NavMailGraph.Msal.dll`) für alle, die Microsofts Authentifizierungsbibliothek
  einsetzen möchten.
* Nachrichten mit Anhängen bis insgesamt ca. 2,5 MB gehen in einem `sendMail`-Aufruf raus (braucht nur
  **Mail.Send**). Die Grenze gilt für die ganze Anfrage inkl. Base64-Kodierung (3,5 MiB); ein 3-MB-PDF ist also
  schon „groß“.
* Größere Nachrichten laufen über Entwurf + Upload-Sitzungen für Anhänge (bis 150 MB pro Anhang; braucht
  zusätzlich **Mail.ReadWrite**). Abschaltbar.
* Festes Absenderpostfach: Absender ist immer dieses Postfach, die Adresse des NAV-Benutzers wird als
  Antwortadresse (Reply-To) gesetzt. Kein „Senden als“-Recht nötig.
* Wiederholungen bei HTTP 429/503/509 unter Beachtung von `Retry-After`. Ein sendMail oder Entwurfsversand mit Timeout oder HTTP 504 wird nie wiederholt; der Fehler meldet einen unklaren Ausgang, damit keine Mail doppelt rausgeht. Token-Cache pro Prozess.
* Fehlertexte mit Graph-/Entra-Fehlercode, Request-ID und Konfigurationshinweis. Optionale Protokolldatei,
  Geheimnisse werden unkenntlich gemacht.
* Clouds: Global, US Government (L4), US Government DoD (L5), China (21Vianet).
* PowerShell-Skripte für Zertifikat, Entra-App-Registrierung, Exchange-RBAC-Bereich und Serverinstallation.
* Build und Unit-Tests auf macOS; der .NET-Framework-Build wird in der CI unter Windows getestet.

## Voraussetzungen

| Komponente | Anforderung |
|------------|-------------|
| Dynamics NAV | NAV 2016 (C/AL, C/SIDE). Objekte erstellt auf Basis CU1 (Build 43402). NAV 2017+ ungetestet. |
| NAV-Server | Windows Server mit **.NET Framework 4.6.2 oder neuer** und **TLS 1.2** (ab Windows Server 2012 R2 standardmäßig aktiv) |
| Microsoft 365 | Exchange Online mit einem Postfach für NAV (empfohlen: freigegebenes Postfach / Shared Mailbox) |
| Entra ID | Recht zum Anlegen einer App-Registrierung (Anwendungsadministrator oder Cloudanwendungsadministrator) |
| Exchange | Mitglied von „Organization Management“ (oder Entra-Rolle „Exchange-Administrator“) für den RBAC-Bereich |
| Admin-Arbeitsplatz | PowerShell 7.6+, Module Microsoft.Graph.Applications und ExchangeOnlineManagement 3.10.x |
| Build-Rechner | .NET SDK 8 (macOS, Linux oder Windows). Ein neueres SDK reicht nur, wenn zusätzlich die .NET-8-Laufzeit installiert ist: Tests und `nmg` zielen auf net8.0 und wechseln nicht automatisch auf eine neuere Hauptversion der Laufzeit. |

NAV 2016 selbst nennt .NET Framework 4.5.2 als Minimum. Das Add-In zielt auf 4.6.2, der Server braucht daher
4.6.2 oder neuer (empfohlen 4.8).

## Schnellstart

1. **Build** (hier macOS; Linux/Windows funktionieren genauso).

   ```bash
   brew install --cask dotnet-sdk@8   # Tests und nmg brauchen die .NET-8-Laufzeit; dotnet-sdk installiert nur die neueste
   ./build.sh
   ```

   Ergebnis: `dist/Add-ins/NavMailGraph/NavMailGraph.dll` (+ optionaler Unterordner `Msal/`), `dist/nav/` und
   `dist/SHA256SUMS`. Alternativ das Release-ZIP herunterladen. Details:
   [docs/development-macos.md](docs/development-macos.md).

2. **Entra-App-Registrierung** mit Zertifikat und **ohne** Graph-Berechtigung.

   ```powershell
   ./deploy/New-NavMailGraphCertificate.ps1 -OutputDirectory ./certs
   $app = ./deploy/New-NavMailGraphApp.ps1 -DisplayName 'NAV2016 MailGraph' -CertificatePath ./certs/NavMailGraph.cer
   ```

   Details und die Variante im Portal: [docs/azure-setup.md](docs/azure-setup.md).

3. **Exchange-Bereich**: der App nur das Senden aus dem NAV-Postfach erlauben.

   ```powershell
   ./deploy/Grant-NavMailGraphMailboxScope.ps1 -AppId $app.ClientId -ServicePrincipalObjectId $app.ServicePrincipalObjectId `
       -DisplayName $app.DisplayName -Mailbox nav@contoso.com -NegativeTestMailbox ceo@contoso.com
   # -IncludeReadWrite ergänzen für Anhänge über insgesamt ca. 2,5 MB (sendMail-Anfrage inkl. Base64 max. 3,5 MiB)
   ```

   Vor dem ersten echten Versand 30 Minuten bis 2 Stunden warten (Berechtigungs-Cache von Exchange).
   Details: [docs/exchange-rbac.md](docs/exchange-rbac.md).

4. **Add-In installieren** auf dem NAV-Server (Windows PowerShell 5.1 als Administrator).

   ```powershell
   .\Install-NavMailGraphAddin.ps1 -ServerInstance DynamicsNAV90 -PfxPath C:\Temp\NavMailGraph.pfx -FixTls
   ```

   Kopiert die DLL, importiert das Zertifikat, gibt dem NAV-Dienstkonto Lesezugriff auf den privaten Schlüssel,
   setzt die TLS-Registrierungswerte und startet die Instanz neu. Details: [docs/deployment.md](docs/deployment.md).

5. **NAV-Objekte**: DLL auf den C/SIDE-Rechner kopieren, Patch für Tabelle 409 anwenden, Codeunit 50400
   importieren, Patches für Codeunit 400 und Seite 409 anwenden, kompilieren. Schritt für Schritt in
   [nav/README.md](nav/README.md) (englisch).

6. **Testen** auf Seite 409 „SMTP Mail Setup“ (SMTP-E-Mail-Einrichtung), Gruppe *Microsoft Graph*: Felder ausfüllen,
   **Graph-Verbindung testen**, dann **Graph-Test-E-Mail senden**. Wenn beides klappt, *Mailversand* =
   `Microsoft Graph` setzen und die Standardaktion **Test Email Setup** (Codeunit 412) ausführen. Bei Fehlern:
   [docs/troubleshooting.md](docs/troubleshooting.md).

## Berechtigungen

| Berechtigung | Wann | Was sie erlaubt |
|--------------|------|-----------------|
| **Mail.Send** (Anwendung) | immer | Senden im Namen des Postfachs und Kopie in „Gesendete Elemente“. Kein Lesen. |
| **Mail.ReadWrite** (Anwendung) | nur für Nachrichten mit Anhängen über insgesamt ca. 2,5 MB (die `sendMail`-Anfrage inkl. Base64-Kodierung ist auf 3,5 MiB begrenzt; ein typisches eingescanntes PDF mit 3 MB braucht es bereits; *Graph: Große Nachrichten* = Ja) | Entwurf anlegen, Anhänge hinzufügen, Upload-Sitzungen, Entwurf löschen. Außerdem E-Mails im Postfach lesen, ändern und löschen. Enthält **kein** Senden. |

Empfehlung: beide Rechte **nur** über Exchange Online RBAC for Applications vergeben (Rollen
`Application Mail.Send` und `Application Mail.ReadWrite`), beschränkt auf das Absenderpostfach, und **nicht** als
Graph-Anwendungsberechtigung mit Administratorzustimmung in Entra ID. Entra- und Exchange-Rechte addieren sich;
eine mandantenweite Entra-Berechtigung hebelt jede Exchange-Beschränkung aus. Erklärung in
[docs/exchange-rbac.md](docs/exchange-rbac.md). Kundeninformation auf einer Seite:
[docs/de/kundeninfo-berechtigungen.md](docs/de/kundeninfo-berechtigungen.md).

## Grenzwerte

| Grenze | Wert | Quelle / Auswirkung |
|--------|------|---------------------|
| Graph-Anfragegröße (`sendMail`) | 4 MB | Das Add-In wechselt ab einer geschätzten Anfrage von 3,5 MiB zum Entwurfsverfahren (Base64 vergrößert um ca. 33 %). |
| Anhang direkt per POST | 3 MB | Größere Anhänge laufen über eine Upload-Sitzung. |
| Upload-Sitzung | 150 MB pro Anhang | Add-In-Standard `MaxAttachmentBytes` = 150 MiB. In US Government L4/L5 nicht verfügbar. |
| Nachrichtengröße Exchange Online | 35 MB (Standard) | Administratoren können 1–150 MB einstellen. Ein größerer Upload gelingt, die Nachricht kommt aber als Unzustellbarkeitsbericht (NDR) zurück. |
| Senderate | 30 Nachrichten pro Minute und Postfach | Überschuss wird gedrosselt und verzögert. |
| Empfängerrate | 10.000 Empfänger pro Tag und Postfach | gleitende 24 Stunden |
| Empfänger pro Nachricht | 500 (Standard) | An + Cc + Bcc; Administratoren können 1–1000 einstellen. |
| Graph-Upload-Volumen | 150 MB pro 5 Minuten je App und Postfach | relevant bei vielen großen Anhängen |

Das Add-In erzwingt die Exchange-Grenzen nicht; Mandanten können sie ändern. Exchange Online ist nicht für
Massenversand gedacht. Ein `202 Accepted` von Graph bedeutet „angenommen“, nicht „zugestellt“: abgelehnte
Empfänger erscheinen später als Unzustellbarkeitsbericht im Absenderpostfach.

## Dokumentation

Die technische Dokumentation ist englisch; die Kundeninformation zu Berechtigungen gibt es auf Deutsch.

| Dokument | Inhalt |
|----------|--------|
| [docs/azure-setup.md](docs/azure-setup.md) | Entra-App-Registrierung (Portal und Skript), Zertifikat oder Secret, Werte für NAV |
| [docs/exchange-rbac.md](docs/exchange-rbac.md) | RBAC for Applications: Beschränkung, Test, Entfernen |
| [docs/deployment.md](docs/deployment.md) | Windows-Server: Installationsskript, manuelle Schritte, Zertifikat, TLS, Proxy, MSAL, Updates |
| [nav/README.md](nav/README.md) | NAV-Objekte: Import, Patches, Konfiguration, Tests, Rückbau |
| [docs/development-macos.md](docs/development-macos.md) | Build, Tests, `nmg`-CLI, Release-Prozess |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Fehlercodes, Ursachen und Lösungen; Protokolldatei |
| [docs/de/kundeninfo-berechtigungen.md](docs/de/kundeninfo-berechtigungen.md) | Kundeninformation zu den Berechtigungen |
| [deploy/README.md](deploy/README.md) | PowerShell-Skripte im Detail |
| [docs/DESIGN.md](docs/DESIGN.md) | Design und API-Vertrag |
| [docs/research-notes.md](docs/research-notes.md) | Geprüfte Fakten mit Microsoft-Quellen |

## Status und Haftungsausschluss

* Version 0.1.0, Vorabversion. Zuerst mit einem Testmandanten und einer NAV-Testdatenbank verwenden.
* Die C/AL-Objekte (Codeunit 50400 und die Patches) werden **nicht in der CI kompiliert**; dort gibt es keinen
  NAV-2016-Compiler. Sie wurden statisch gegen Objektexporte von NAV 2016 CU1 geprüft. Vor dem Produktiveinsatz in
  C/SIDE importieren, kompilieren und testen.
* Die Microsoft-Objekte (Codeunit 400, Tabelle 409, Seite 409) werden nicht weitergegeben. Das Repository enthält
  nur Patch-Anleitungen.
* Dieses Projekt steht **in keiner Verbindung zu Microsoft** und wird von Microsoft weder unterstützt noch
  empfohlen. Microsoft, Microsoft 365, Exchange Online, Microsoft Entra und Dynamics NAV sind Marken der
  Microsoft-Unternehmensgruppe.
* Sicherheitsprobleme: siehe [SECURITY.md](SECURITY.md). Beiträge: siehe [CONTRIBUTING.md](CONTRIBUTING.md).

## Lizenz

[MIT](LICENSE)
