# Kundeninformation: Berechtigungen der App „NAV2016 MailGraph“

**Frage:** Warum braucht die App die Berechtigung *Mail.Send*, und kann sie damit alle Postfächer lesen oder aus
allen Postfächern senden? Und was ändert sich, wenn NAV ab Version 0.2 auch E-Mails abholt?

**Kurz:** *Mail.Send* erlaubt nur das **Senden**, nicht das Lesen. In der empfohlenen Einrichtung gilt die
Berechtigung **nur für das Absenderpostfach von Microsoft Dynamics NAV** (z. B. `nav@ihrefirma.de`). Für alle
anderen Postfächer lehnt Exchange Online den Zugriff ab. Sie können das selbst prüfen und jederzeit widerrufen.

Soll NAV zusätzlich E-Mails **abholen** (z. B. Eingangsrechnungen), braucht die App für dieses eine Postfach ein
Leserecht. Damit kann sie **alle** E-Mails und Anhänge in diesem Postfach lesen – nicht nur die, die NAV
weiterverarbeitet. Deshalb empfehlen wir ein eigenes Postfach nur für den NAV-Import (Abschnitt
[E-Mails abholen](#e-mails-abholen-ab-version-02-optional)).

## Was die App tut

Microsoft Dynamics NAV 2016 versendet Belege (Rechnungen, Mahnungen, Berichte) bisher per SMTP. Diese Erweiterung
ersetzt SMTP durch die Microsoft-Graph-Schnittstelle `sendMail`. NAV meldet sich dabei nicht als Benutzer an,
sondern als Anwendung (App-Registrierung in Microsoft Entra ID). Es wird kein Benutzerkennwort gespeichert.

## Welche Rechte und wofür

| Berechtigung | Wann | Was sie erlaubt | Was sie **nicht** erlaubt |
|--------------|------|-----------------|---------------------------|
| **Mail.Send** (Anwendung) | immer | E-Mails im Namen des Postfachs senden und eine Kopie in „Gesendete Elemente“ ablegen | E-Mails lesen, Ordner ansehen, Kalender, Kontakte, Dateien |
| **Mail.ReadWrite** (Anwendung) | **nur**, wenn NAV Nachrichten mit Anhängen über insgesamt ca. 2,5 MB senden soll (die `sendMail`-Anfrage inkl. Base64-Kodierung ist auf 3,5 MiB begrenzt; ein eingescanntes PDF mit 3 MB braucht diese Berechtigung bereits) – oder wenn NAV abgeholte E-Mails verschieben, als gelesen markieren oder löschen soll (ab 0.2) | Entwürfe anlegen, Anhänge hochladen, Entwurf löschen. Technisch auch: E-Mails im Postfach **lesen, ändern, verschieben und löschen**, Ordner anlegen | Senden (dafür bleibt Mail.Send nötig) |
| **Mail.Read** (Anwendung) | **nur**, wenn NAV E-Mails abholen soll, ohne das Postfach zu verändern (ab 0.2, Lesemodus) | E-Mails, Nachrichtentexte und Anhänge im Postfach **lesen** | Ändern, Verschieben, Löschen, Senden |

Zu *Mail.ReadWrite*, ehrlich gesagt: Diese Berechtigung gibt der App Lesezugriff auf die E-Mails im betroffenen
Postfach. Microsoft Graph erlaubt große Anhänge (über 3 MB) nur über einen Entwurf, und dafür ist diese
Berechtigung vorgeschrieben. Die App nutzt sie nur, um den eigenen Entwurf anzulegen, Anhänge hochzuladen und den
Entwurf bei einem Fehler wieder zu löschen. Wenn NAV keine großen Anhänge versenden muss, vergeben Sie
*Mail.ReadWrite* **nicht** und stellen in NAV „Graph: Große Nachrichten“ auf Nein. Dann werden zu große Nachrichten
mit einer klaren Fehlermeldung abgelehnt.

Bei Beschränkung auf das NAV-Postfach (nächster Abschnitt) gilt auch *Mail.ReadWrite* nur für dieses Postfach.
Ein eigenes, gemeinsames Postfach nur für NAV (Shared Mailbox) enthält typischerweise nur die von NAV gesendeten
Nachrichten und eventuelle Antworten bzw. Unzustellbarkeitsberichte.

## E-Mails abholen (ab Version 0.2, optional)

NAV kann ein Postfach regelmäßig abfragen (z. B. alle 5 Minuten über die Aufgabenwarteschlange), neue E-Mails mit
Anhängen in NAV-Tabellen übernehmen und daraus z. B. Einkaufsrechnungen vorbereiten. Ehrlich gesagt bedeutet das:

* **Ein Leserecht gilt für das ganze Postfach.** Weder Microsoft Graph noch Exchange bieten ein Recht „nur
  bestimmte E-Mails“ oder „nur einen Ordner“. Die App kann daher jede E-Mail in dem freigegebenen Postfach lesen,
  einschließlich Anhängen. NAV selbst liest nur den eingestellten Ordner (z. B. „Posteingang“).
* **Eigenes Postfach empfohlen.** Richten Sie für den Import ein eigenes, gemeinsames Postfach ein (z. B.
  `rechnungseingang@ihrefirma.de`) und geben Sie der App nur dieses frei – nie das Postfach einer Person. Dann
  sieht die App nur, was an diese Adresse geschickt wurde.
* **Standard: verschieben.** Nach dem Import verschiebt NAV die E-Mail in den Unterordner „Processed“ (bei Fehlern
  „Error“). So sieht man im Postfach, was noch offen ist. Dafür ist *Mail.ReadWrite* nötig (Rolle „Application
  Mail.ReadWrite“). Damit könnte die App E-Mails in diesem Postfach auch ändern oder löschen; NAV tut das nur, wenn
  Sie die Option „Löschen“ wählen.
* **Lesemodus ohne Schreibrecht.** Wer der App kein Schreibrecht geben möchte, stellt in NAV „Nach dem Import“ auf
  „Nichts“ (und den Abrufmodus auf „Delta-Abfrage“). Dann genügt *Mail.Read* (Rolle „Application Mail.Read“); das
  Postfach bleibt unverändert, NAV merkt sich selbst, was schon importiert ist.
* *Mail.ReadBasic* reicht nicht aus, weil es keine Nachrichtentexte und Anhänge umfasst.
* Die importierten E-Mails und Anhänge liegen danach in der NAV-Datenbank bzw. im eingestellten Objektspeicher
  (nächster Abschnitt) und unterliegen den dortigen Berechtigungen und Aufbewahrungsregeln.

Die Beschränkung auf einzelne Postfächer funktioniert genauso wie beim Senden (nächste Abschnitte): Rolle in
Exchange vergeben, **keine** Mail-Berechtigung in Entra ID. Eine mandantenweite *Mail.Read*-Berechtigung in Entra
ID würde das Lesen **aller** Postfächer erlauben.

## Objektspeicher für große Anhänge (ab Version 0.2, optional)

Große Anhänge kann NAV in einem eigenen Speicher ablegen statt in der Datenbank: Azure Blob Storage oder Amazon S3
(bzw. ein S3-kompatibler Anbieter). Beim Senden kann NAV große Anhänge dort ablegen und in der E-Mail nur einen
zeitlich begrenzten Download-Link verschicken.

* **Azure mit Entra ID:** Die App bekommt die Azure-Rolle *Storage Blob Data Contributor* nur auf den einen
  Container (lesen, schreiben, löschen von Dateien in diesem Container). Es wird kein Speicherschlüssel in NAV
  gespeichert. Download-Links sind in diesem Modus nicht möglich.
* **Azure mit Kontoschlüssel oder SAS-URL, Amazon S3:** Der Schlüssel wird in NAV verschlüsselt gespeichert (Tabelle
  1261, wie das Client-Secret). Ein Azure-Kontoschlüssel öffnet das gesamte Speicherkonto – daher ein eigenes
  Speicherkonto nur für NAV verwenden. Für S3 empfehlen wir einen eigenen IAM-Benutzer, der nur einen Pfad im
  Bucket lesen, schreiben und löschen darf.
* **Download-Links** funktionieren für jeden, der den Link hat (auch nach Weiterleitung der E-Mail), bis sie
  ablaufen – standardmäßig 7 Tage, bei S3 höchstens 7 Tage. Die Dateien bleiben im Speicher, bis eine
  Aufbewahrungsregel sie löscht.

## Beschränkung auf das NAV-Absenderpostfach

Ohne Einschränkung gilt eine in Entra ID erteilte Anwendungsberechtigung *Mail.Send* für **alle** Postfächer des
Mandanten. Deshalb empfehlen wir eine andere Einrichtung:

1. Die App erhält in Entra ID **keine** Microsoft-Graph-Mail-Berechtigung und keine Administratorzustimmung.
2. Die Rechte werden stattdessen in **Exchange Online** über **„RBAC for Applications“** vergeben – mit einem
   Bereich (Management Scope), der **nur das NAV-Postfach** enthält (Rollen „Application Mail.Send“ und bei Bedarf
   „Application Mail.ReadWrite“ bzw. „Application Mail.Read“; Postfächer für den Import gehören in denselben oder
   einen eigenen Bereich).
3. Zugriffe auf jedes andere Postfach beantwortet Microsoft Graph mit „Zugriff verweigert“ (HTTP 403).

Wichtig: Beides darf nicht kombiniert werden. Microsoft wertet Entra-Berechtigungen und Exchange-Berechtigungen
als Vereinigungsmenge. Steht in Entra ID zusätzlich *Mail.Send* mit Administratorzustimmung, ist die Beschränkung
wirkungslos. Prüfen Sie daher in Entra ID unter *Unternehmensanwendungen → App → Berechtigungen*, dass dort
**keine** Mail-Anwendungsberechtigung eingetragen ist.

Änderungen an den Exchange-Berechtigungen werden laut Microsoft nach 30 Minuten bis 2 Stunden wirksam.

## So prüfen Sie es selbst

In PowerShell mit dem Modul *ExchangeOnlineManagement* (Rolle „Organization Management“ oder
„Exchange-Administrator“):

```powershell
Connect-ExchangeOnline
# <ObjektID> = Objekt-ID der Unternehmensanwendung „NAV2016 MailGraph“ in Entra ID
Test-ServicePrincipalAuthorization -Identity <ObjektID> -Resource nav@ihrefirma.de          # erwartet: InScope = True
Test-ServicePrincipalAuthorization -Identity <ObjektID> -Resource geschaeftsfuehrung@ihrefirma.de   # erwartet: InScope = False
```

Die Ausgabe zeigt Rolle (`RoleName`), erteilte Rechte (`GrantedPermissions`), den Bereich
(`AllowedResourceScope`) und ob das Postfach im Bereich liegt (`InScope`). Hinweis: Dieser Test berücksichtigt
**nur** Exchange-Berechtigungen, keine Entra-Berechtigungen – deshalb zusätzlich die Entra-Prüfung aus dem
vorigen Abschnitt.

## Anmeldung mit Zertifikat statt Kennwort

* Wir empfehlen ein **Zertifikat**. Der private Schlüssel wird als kennwortgeschützte .pfx-Datei erzeugt und auf
  dem NAV-Server nicht exportierbar in den Zertifikatspeicher importiert; außer einer Sicherung in einem Tresor
  werden alle Kopien gelöscht. In Entra ID ist nur der öffentliche Teil hinterlegt. Microsoft empfiehlt Zertifikate
  ausdrücklich gegenüber geheimen Clientschlüsseln.
* Ein geheimer Clientschlüssel („Secret“) ist möglich, aber nur für Tests vorgesehen. Er wird in NAV
  verschlüsselt gespeichert (sofern die Datenbankverschlüsselung eingerichtet ist), läuft nach höchstens
  24 Monaten ab; Microsoft empfiehlt weniger als 12 Monate.
* Unser Skript erstellt Zertifikate mit höchstens 12 Monaten Laufzeit (Microsoft-Dokumentation für die
  Graph-API); sie werden jährlich erneuert.

## Protokollierung

* **Entra ID – Anmeldeprotokolle** (*Sign-in logs → Dienstprinzipalanmeldungen*): jede Token-Anforderung der App
  mit Zeitpunkt und Ergebnis.
* **Microsoft Graph-Aktivitätsprotokolle** (müssen über Diagnoseeinstellungen aktiviert werden; Voraussetzungen
  siehe Microsoft Learn): jeder Graph-Aufruf mit App-ID, aufgerufener Adresse und Statuscode.
* **Exchange-Nachrichtenablaufverfolgung**: welche Nachrichten das NAV-Postfach versendet hat.
* Auf dem NAV-Server kann optional eine Protokolldatei geschrieben werden. Kennwörter, Schlüssel und Tokens
  werden darin unkenntlich gemacht; Betreff, Adressen und Anhangnamen sind enthalten, der Nachrichtentext nicht.

## Widerruf – jederzeit

Jede der folgenden Maßnahmen beendet den Versand über die App (Verzögerungen siehe unten):

* Exchange-Rollenzuweisung entfernen (`Remove-ManagementRoleAssignment`) – wirksam nach 30 Minuten bis 2 Stunden.
* Zertifikat bzw. geheimen Schlüssel in der App-Registrierung löschen – neue Anmeldungen schlagen sofort fehl.
* Unternehmensanwendung deaktivieren oder löschen.

Wichtig: Bereits ausgestellte Zugriffstoken bleiben bis zu ihrem Ablauf gültig (die Laufzeit legt Microsoft
beim Ausstellen fest, typischerweise rund eine Stunde, teils etwas mehr); der NAV-Server verwendet sie bis dahin
weiter. Für einen sofortigen Stopp zusätzlich in NAV „Mailversand“ = SMTP setzen oder die NAV-Serverinstanz neu
starten.

In NAV kann jederzeit auf den bisherigen SMTP-Versand zurückgeschaltet werden (Feld „Mailversand“ = SMTP).

## Zusammenfassung

| Frage | Antwort |
|-------|---------|
| Kann die App E-Mails lesen? | Mit *Mail.Send* allein: nein. Mit *Mail.ReadWrite* (große Anhänge, Verschieben abgeholter E-Mails) oder *Mail.Read* (Lesemodus): ja, und zwar alle E-Mails in den freigegebenen Postfächern – andere Postfächer nicht. |
| Kann die App E-Mails ändern oder löschen? | Nur mit *Mail.ReadWrite*, nur in den freigegebenen Postfächern. NAV verschiebt standardmäßig nach „Processed“/„Error“ und löscht nur, wenn das eingestellt ist. |
| Wo liegen abgeholte Anhänge? | In der NAV-Datenbank, in einem Serverordner oder im eingestellten Objektspeicher (Azure/S3). |
| Kann die App aus anderen Postfächern senden? | Bei Einrichtung über RBAC for Applications ohne Entra-Berechtigung: nein. |
| Wie prüfe ich das? | `Test-ServicePrincipalAuthorization` und Entra-Berechtigungsliste der App. |
| Wie wird angemeldet? | Zertifikat auf dem NAV-Server (empfohlen), kein Benutzerkennwort. |
| Wo sehe ich Zugriffe? | Entra-Anmeldeprotokolle, Graph-Aktivitätsprotokolle, Nachrichtenablaufverfolgung. |
| Wie widerrufe ich? | Rollenzuweisung entfernen, Zertifikat/Secret löschen oder App deaktivieren. Bereits ausgestellte Token gelten bis zu ihrem Ablauf; sofort wirkt „Mailversand“ = SMTP oder ein Neustart der NAV-Serverinstanz. |

Technische Details (englisch): [Exchange RBAC](../exchange-rbac.md), [App-Registrierung](../azure-setup.md),
[E-Mails lesen](../reading-mail.md), [Objektspeicher](../object-storage.md).
Diese Erweiterung ist ein Open-Source-Projekt und steht in keiner Verbindung zu Microsoft.
