# Kundeninformation: Berechtigungen der App „NAV2016 MailGraph“

**Frage:** Warum braucht die App die Berechtigung *Mail.Send*, und kann sie damit alle Postfächer lesen oder aus
allen Postfächern senden?

**Kurz:** *Mail.Send* erlaubt nur das **Senden**, nicht das Lesen. In der empfohlenen Einrichtung gilt die
Berechtigung **nur für das Absenderpostfach von Microsoft Dynamics NAV** (z. B. `nav@ihrefirma.de`). Für alle
anderen Postfächer lehnt Exchange Online den Zugriff ab. Sie können das selbst prüfen und jederzeit widerrufen.

## Was die App tut

Microsoft Dynamics NAV 2016 versendet Belege (Rechnungen, Mahnungen, Berichte) bisher per SMTP. Diese Erweiterung
ersetzt SMTP durch die Microsoft-Graph-Schnittstelle `sendMail`. NAV meldet sich dabei nicht als Benutzer an,
sondern als Anwendung (App-Registrierung in Microsoft Entra ID). Es wird kein Benutzerkennwort gespeichert.

## Welche Rechte und wofür

| Berechtigung | Wann | Was sie erlaubt | Was sie **nicht** erlaubt |
|--------------|------|-----------------|---------------------------|
| **Mail.Send** (Anwendung) | immer | E-Mails im Namen des Postfachs senden und eine Kopie in „Gesendete Elemente“ ablegen | E-Mails lesen, Ordner ansehen, Kalender, Kontakte, Dateien |
| **Mail.ReadWrite** (Anwendung) | **nur**, wenn NAV Nachrichten mit Anhängen über insgesamt ca. 2,5 MB senden soll (die `sendMail`-Anfrage inkl. Base64-Kodierung ist auf 3,5 MiB begrenzt; ein eingescanntes PDF mit 3 MB fällt bereits darunter) | Entwürfe anlegen, Anhänge hochladen, Entwurf löschen. Technisch auch: E-Mails im Postfach **lesen, ändern und löschen** | Senden (dafür bleibt Mail.Send nötig) |

Zu *Mail.ReadWrite*, ehrlich gesagt: Diese Berechtigung gibt der App Lesezugriff auf die E-Mails im betroffenen
Postfach. Microsoft Graph erlaubt große Anhänge (über 3 MB) nur über einen Entwurf, und dafür ist diese
Berechtigung vorgeschrieben. Die App nutzt sie nur, um den eigenen Entwurf anzulegen, Anhänge hochzuladen und den
Entwurf bei einem Fehler wieder zu löschen. Wenn NAV keine großen Anhänge versenden muss, vergeben Sie
*Mail.ReadWrite* **nicht** und stellen in NAV „Graph: Große Nachrichten“ auf Nein. Dann werden zu große Nachrichten
mit einer klaren Fehlermeldung abgelehnt.

Bei Beschränkung auf das NAV-Postfach (nächster Abschnitt) gilt auch *Mail.ReadWrite* nur für dieses Postfach.
Ein eigenes, gemeinsames Postfach nur für NAV (Shared Mailbox) enthält typischerweise nur die von NAV gesendeten
Nachrichten und eventuelle Antworten bzw. Unzustellbarkeitsberichte.

## Beschränkung auf das NAV-Absenderpostfach

Ohne Einschränkung gilt eine in Entra ID erteilte Anwendungsberechtigung *Mail.Send* für **alle** Postfächer des
Mandanten. Deshalb empfehlen wir eine andere Einrichtung:

1. Die App erhält in Entra ID **keine** Microsoft-Graph-Mail-Berechtigung und keine Administratorzustimmung.
2. Die Rechte werden stattdessen in **Exchange Online** über **„RBAC for Applications“** vergeben – mit einem
   Bereich (Management Scope), der **nur das NAV-Postfach** enthält (Rollen „Application Mail.Send“ und bei Bedarf
   „Application Mail.ReadWrite“).
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
| Kann die App E-Mails lesen? | Mit *Mail.Send* allein: nein. Mit *Mail.ReadWrite* (nur für große Anhänge): ja, im NAV-Postfach. |
| Kann die App aus anderen Postfächern senden? | Bei Einrichtung über RBAC for Applications ohne Entra-Berechtigung: nein. |
| Wie prüfe ich das? | `Test-ServicePrincipalAuthorization` und Entra-Berechtigungsliste der App. |
| Wie wird angemeldet? | Zertifikat auf dem NAV-Server (empfohlen), kein Benutzerkennwort. |
| Wo sehe ich Zugriffe? | Entra-Anmeldeprotokolle, Graph-Aktivitätsprotokolle, Nachrichtenablaufverfolgung. |
| Wie widerrufe ich? | Rollenzuweisung entfernen, Zertifikat/Secret löschen oder App deaktivieren. Bereits ausgestellte Token gelten bis zu ihrem Ablauf; sofort wirkt „Mailversand“ = SMTP oder ein Neustart der NAV-Serverinstanz. |

Technische Details (englisch): [Exchange RBAC](../exchange-rbac.md), [App-Registrierung](../azure-setup.md).
Diese Erweiterung ist ein Open-Source-Projekt und steht in keiner Verbindung zu Microsoft.
