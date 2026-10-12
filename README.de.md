# nav2016-mailgraph

E-Mails aus **Microsoft Dynamics NAV 2016** über **Microsoft Graph** statt über SMTP versenden.

[English version](README.md)

Ein kleines .NET-Add-In (`NavMailGraph.dll`) und eine neue C/AL-Codeunit ersetzen den SMTP-Versand hinter
Codeunit 400 „SMTP Mail“. Alle bestehenden Aufrufer (Belegversand, Mahnungen, Berichtsversand,
Benachrichtigungs-E-Mails, eigener Code) funktionieren unverändert weiter. Der Versand läuft über
`POST /users/{postfach}/sendMail` mit Anwendungsberechtigungen (OAuth 2.0 Client Credentials).

Version 0.2 ergänzt die Gegenrichtung und große Dateien: Eine Codeunit für die Aufgabenwarteschlange **liest
Postfächer** und importiert E-Mails und Anhänge in NAV-Tabellen, ein Integrationsereignis übergibt sie an Ihren
Code (z. B. „aus jedem PDF eine Einkaufsrechnung anlegen“), und große Anhänge landen in **Azure Blob Storage oder
Amazon S3** – in beide Richtungen, einschließlich „Download-Link statt 50-MB-Anhang versenden“.

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
    dll -. "0.2: E-Mails lesen (Aufgabenwarteschlange)" .-> graph
    dll -. "0.2: große Anhänge, Links" .-> storage[("Azure Blob Storage<br/>oder Amazon S3")]
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

Neu in 0.2 ([docs/reading-mail.md](docs/reading-mail.md), [docs/large-attachments.md](docs/large-attachments.md),
[docs/object-storage.md](docs/object-storage.md), englisch):

* **Postfächer lesen** aus der NAV-Aufgabenwarteschlange: eine Einrichtungszeile pro Postfach, Ordnerpfade wie
  `Inbox/Rechnungen`, unveränderliche Graph-IDs, Import in NAV-Tabellen (Kopf, Text, Anhänge, Original-`.eml` bei
  Bedarf).
* Nach dem Import wird die E-Mail **nach `<Ordner>/Processed` verschoben** (Standard; Fehler nach
  `<Ordner>/Error`), als gelesen markiert, gelöscht oder unverändert gelassen. Abruf über ein **Empfangsdatum als
  Wasserzeichen** mit Duplikatprüfung oder ein **reiner Lesemodus per Delta-Abfrage**, der nur Mail.Read braucht.
* **Integrationsereignisse** `OnBeforeImportMail`, `OnAfterImportAttachment`, `OnAfterImportMail` für
  Partnercode; ein fehlschlagender Subscriber setzt nur diese E-Mail auf Fehler.
* **Große eingehende Anhänge** werden gestreamt (`$value`, nie Base64 im Speicher): bis zu einer Größengrenze in
  ein NAV-BLOB, darüber **direkt von Graph in den Objektspeicher**, ohne temporäre Datei.
* **Objektspeicher** in der Kern-DLL, weiterhin ohne NuGet-Abhängigkeiten: Azure Blob (Shared Key, SAS-URL oder
  Entra-ID-Token derselben App) und Amazon S3 / S3-kompatible Speicher (SigV4, Path-Style für R2, Wasabi,
  SeaweedFS ...), Block-/Multipart-Uploads, Auflisten, Löschen, zeitlich begrenzte Download-Links.
* **Link statt Anhang** beim Senden: Anhänge ab einer Schwelle werden hochgeladen und im Text durch einen
  Download-Link ersetzt; archivierte Dateien lassen sich direkt aus dem Speicher anhängen.
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

4. **Add-In installieren** auf dem NAV-Server (Windows PowerShell 5.1 als Administrator, im Ordner `deploy\`
   des entpackten Release-ZIPs oder des Repositorys nach `./build.sh`).

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

7. **Optional (0.2): Postfach lesen.** Der App das Postfach freigeben (`Grant-NavMailGraphMailboxScope.ps1
   -Mailbox rechnungen@contoso.com -IncludeReadWrite`, für den reinen Lesemodus `-IncludeRead`), die v0.2-Objekte
   importieren ([nav/README.md](nav/README.md#installation-v02)), eine Zeile in **Graph Mailbox Setup** anlegen,
   **Test Connection** und **Poll Now** ausführen, dann **Schedule Polling**. Konzepte:
   [docs/reading-mail.md](docs/reading-mail.md).

8. **Optional (0.2): Objektspeicher.** Ein **Object Storage Profile** (Azure Blob oder S3) anlegen, **Test
   Connection** und **Upload Test File** ausführen und es dann für große eingehende Anhänge oder als *Graph Link
   Storage Profile* für den Versand verwenden. Einrichtung und Sicherheit:
   [docs/object-storage.md](docs/object-storage.md).

## Berechtigungen

| Berechtigung | Wann | Was sie erlaubt |
|--------------|------|-----------------|
| **Mail.Send** (Anwendung) | immer | Senden im Namen des Postfachs und Kopie in „Gesendete Elemente“. Kein Lesen. |
| **Mail.ReadWrite** (Anwendung) | Senden: nur für Nachrichten mit Anhängen über insgesamt ca. 2,5 MB (die `sendMail`-Anfrage inkl. Base64-Kodierung ist auf 3,5 MiB begrenzt; ein typisches eingescanntes PDF mit 3 MB braucht es bereits; *Graph: Große Nachrichten* = Ja). Lesen (0.2): für den Standardmodus, der verarbeitete E-Mails verschiebt, sowie für „als gelesen markieren“ und „löschen“ | Entwurf anlegen, Anhänge hinzufügen, Upload-Sitzungen, Entwurf löschen. E-Mails im Postfach lesen, markieren, verschieben und löschen, Ordner anlegen. Enthält **kein** Senden. |
| **Mail.Read** (Anwendung) | Lesen (0.2) im reinen Lesemodus (*After Import* = Nothing); nicht nötig, wenn Mail.ReadWrite vergeben ist | E-Mails, Texte und Anhänge des Postfachs lesen. Keine Änderungen, kein Senden. Mail.ReadBasic reicht nicht (kein Text, keine Anhänge). |
| **Storage Blob Data Contributor** (Azure-RBAC-Rolle) | nur für Objektspeicher im Azure-Modus *Entra ID* (0.2), auf dem Container | Blobs in diesem Container lesen, schreiben und löschen. Profile mit Shared Key, SAS-URL oder S3 nutzen stattdessen eigene Schlüssel. |

Empfehlung: die Mail-Rechte **nur** über Exchange Online RBAC for Applications vergeben (Rollen
`Application Mail.Send`, `Application Mail.ReadWrite`, `Application Mail.Read`), beschränkt auf die NAV-Postfächer,
und **nicht** als Graph-Anwendungsberechtigung mit Administratorzustimmung in Entra ID. Entra- und Exchange-Rechte
addieren sich; eine mandantenweite Entra-Berechtigung hebelt jede Exchange-Beschränkung aus – bei Mail.Read hieße
das: **jedes** Postfach ist lesbar. Erklärung in [docs/exchange-rbac.md](docs/exchange-rbac.md) und
[docs/reading-mail.md](docs/reading-mail.md#permissions). Kundeninformation auf einer Seite:
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
| Graph-Anfragen (Lesen) | 10.000 pro 10 Minuten und 4 gleichzeitig je App und Postfach | Das Add-In stellt höchstens 4 Anfragen pro Postfach gleichzeitig und wiederholt 429/503 unter Beachtung von `Retry-After`. |
| Vorsignierter S3-Link | 7 Tage | Eine längere Gültigkeit schlägt fehl, bevor etwas hochgeladen wird. Azure-Shared-Key-Links haben diese Grenze nicht. |

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
| [docs/reading-mail.md](docs/reading-mail.md) | Postfächer lesen: Verarbeitungsmodi, Wasserzeichen, Delta, Berechtigungen, Aufgabenwarteschlange, Ereignisse, Grenzen |
| [docs/object-storage.md](docs/object-storage.md) | Azure Blob (Shared Key, SAS, Entra ID) und S3: Einrichtung, IAM-Richtlinie, Anbieter, Schlüsselaufbau, Sicherheit |
| [docs/large-attachments.md](docs/large-attachments.md) | Große Anhänge beim Senden (Entwurf, Upload-Sitzung, Link) und Empfangen (Streaming, BLOB-Grenze, Speicher) |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Fehlercodes, Ursachen und Lösungen; Protokolldatei |
| [docs/de/kundeninfo-berechtigungen.md](docs/de/kundeninfo-berechtigungen.md) | Kundeninformation zu den Berechtigungen |
| [deploy/README.md](deploy/README.md) | PowerShell-Skripte im Detail |
| [docs/DESIGN.md](docs/DESIGN.md), [docs/DESIGN-v2.md](docs/DESIGN-v2.md) | Design und API-Vertrag (v0.1, v0.2) |
| [docs/research-notes.md](docs/research-notes.md), [docs/research-notes-v2.md](docs/research-notes-v2.md) | Geprüfte Fakten mit Quellen von Microsoft, AWS und zu NAV |
| [CHANGELOG.md](CHANGELOG.md) | Änderungen je Version |

## Status und Haftungsausschluss

* Version 0.2.0 in Entwicklung (0.1.0 = nur Versand), Vorabversion. Zuerst mit einem Testmandanten und einer
  NAV-Testdatenbank verwenden.
* Die C/AL-Objekte (Codeunits 50400–50404, Tabellen/Seiten 504xx und die Patches) werden **nicht in der CI
  kompiliert**; dort gibt es keinen NAV-2016-Compiler. Sie wurden statisch gegen Objektexporte von NAV 2016 CU1
  geprüft (`nav/tools/check_cal.py`). Vor dem Produktiveinsatz in C/SIDE importieren, kompilieren und testen.
* Der Objektspeicher wurde gegen die Emulatoren Azurite und SeaweedFS getestet, das Lesen über Graph gegen einen
  simulierten Graph-Server in den Unit-Tests. Vor dem Produktiveinsatz mit dem echten Mandanten, Speicherkonto bzw.
  Bucket testen.
* Die Microsoft-Objekte (Codeunit 400, Tabelle 409, Seite 409) werden nicht weitergegeben. Das Repository enthält
  nur Patch-Anleitungen.
* Dieses Projekt steht **in keiner Verbindung zu Microsoft** und wird von Microsoft weder unterstützt noch
  empfohlen. Microsoft, Microsoft 365, Exchange Online, Microsoft Entra und Dynamics NAV sind Marken der
  Microsoft-Unternehmensgruppe.
* Sicherheitsprobleme: siehe [SECURITY.md](SECURITY.md). Beiträge: siehe [CONTRIBUTING.md](CONTRIBUTING.md).

## Lizenz

[MIT](LICENSE)
