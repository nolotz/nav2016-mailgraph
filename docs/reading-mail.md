# Reading mail (v0.2)

NAV 2016 can poll Exchange Online mailboxes through Microsoft Graph, import e-mails and attachments into NAV
tables and hand them to your own code (for example "create a purchase invoice from every PDF in
`invoices@contoso.com`"). This page explains the concepts. The NAV setup steps are in
[../nav/README.md](../nav/README.md#reading-mail-and-object-storage-v02); large attachments and object storage have
their own pages: [large-attachments.md](large-attachments.md) and [object-storage.md](object-storage.md).

* [Building blocks](#building-blocks)
* [Processing modes: move (default), mark as read, delete, read-only](#processing-modes)
* [Watermark and de-duplication](#watermark-and-de-duplication)
* [Read-only mode with delta query](#read-only-mode-with-delta-query)
* [Permissions](#permissions)
* [Scheduling with the job queue](#scheduling-with-the-job-queue)
* [Events for partners](#events-for-partners)
* [Limits and throttling](#limits-and-throttling)
* [Using the add-in directly](#using-the-add-in-directly)

## Building blocks

| Layer | What |
|-------|------|
| Add-in (`NavMailGraph.dll`) | `GraphConnection` (tenant, app, credential, cloud – configured once), `GraphMailbox` (list, delta, mark, move, delete, folders, streamed downloads), `GraphMailItem`, `GraphMailAttachmentInfo` |
| NAV | Table 50400 *Graph Mailbox Setup* (one line per polled mailbox), Table 50401 *Graph Inbound Mail*, Table 50402 *Graph Inbound Attachment*, Codeunit 50401 *Graph Mailbox Polling* (job queue), Codeunit 50403 *Graph Inbound Mail Mgt.* (open attachment, .eml, body text) |
| Credentials | the Graph fields of Table 409 *SMTP Mail Setup* – the same app registration that sends mail. Nothing is entered twice. |

Why polling and not push notifications: Graph change notifications need a public HTTPS endpoint that answers
Microsoft's validation request within 10 seconds. An on-premises NAV 2016 service tier normally has none, so the
add-in polls ([research-notes-v2.md](research-notes-v2.md#microsoft-graph-reading-and-polling-mail)).

Every Graph call of `GraphMailbox` sends `Prefer: IdType="ImmutableId"`. Message and attachment ids then stay the
same when a mail is moved to another folder of the same mailbox. They change only when a mail moves to an archive
mailbox or is exported and imported again. The ids are **case-sensitive**; NAV stores them in Text fields (never
Code) and compares them exactly.

## Processing modes

What happens to a mail in Exchange after NAV imported it is a per-mailbox setting (*After Import* on the mailbox
card):

| *After Import* | What the add-in does | Permission |
|----------------|----------------------|------------|
| **Move to Processed Folder** (default) | `POST .../messages/{id}/move` to *Processed Folder* (default `<Folder>/Processed`); failed mails go to *Error Folder* (default `<Folder>/Error`). Missing folders are created. | Mail.ReadWrite |
| Mark as Read | `PATCH {"isRead": true}`; failed mails go to the error folder | Mail.ReadWrite |
| Delete | `DELETE .../messages/{id}`; failed mails go to the error folder. Microsoft's API page does not say whether the mail lands in *Deleted Items* or *Recoverable Items*; test it before you rely on it. | Mail.ReadWrite |
| Nothing | the mailbox is not changed | Mail.Read |

**Why "move" is the default.** The source folder becomes a visible work queue: whatever is still in `Inbox` has
not been processed, `Inbox/Error` holds what failed, `Inbox/Processed` the rest. Unread status is **not** a reliable signal: a user who opens the mail in
Outlook marks it as read, and an "unread only" poller would silently skip it. That is why `UnreadOnly` exists but
is off by default.

Folder paths such as `Inbox/Invoices/Processed` are resolved segment by segment: the first segment may be a
well-known name (`inbox`, `archive`, `deleteditems`, `junkemail`, `sentitems`, ... – these work in every mailbox
language), otherwise a folder name directly below the mailbox root; further segments are child folders by display
name. Resolved ids are cached per `GraphMailbox` instance; a stale id (folder deleted and recreated) is resolved
again once.

## Watermark and de-duplication

In the default mode (*Polling Mode* = *Received Date Watermark*) every run lists the source folder oldest first:

```
GET /users/{mailbox}/mailFolders/{folder}/messages
    ?$filter=receivedDateTime ge {watermark} [and isRead eq false]
    &$orderby=receivedDateTime asc&$top=...&$select=...
```

* The **watermark** is text: the exact `receivedDateTime` string Graph returned for the newest imported mail
  (`GraphMailItem.ReceivedText`, e.g. `2026-10-12T08:00:00Z`). NAV stores it in *Last Received (Text)* and passes it
  back unchanged as `GraphMailbox.ReceivedAfter`. No DateTime conversion happens on the way, so the NAV time zone
  settings cannot shift it.
* The filter is **inclusive** (`ge`). Mails with the same timestamp are read again; NAV skips them because it
  de-duplicates by Graph id and by `internetMessageId` (the RFC 2822 Message-ID).
* `receivedDateTime` must be the **first** `$filter` property when the query sorts by it. Otherwise Graph answers
  `InefficientFilter`. The add-in builds the query that way; you only see this error if you change the query code.
* *Import From Date* bounds the very first run. Without it the first run reads the whole folder,
  *Max. Mails per Run* (default 50, at most 1000) per run.
* `MoreAvailable` tells NAV that more matching mails exist; the next run continues from the new watermark.

In NAV the order per mail is: duplicate check → import (own transaction, `COMMIT`) → `OnAfterImportMail`
(own transaction) → mailbox action. A crash between the NAV commit and the Graph move therefore leads to a
harmless re-read that the duplicate check skips. If the crash happens after the import but before
`OnAfterImportMail` ran, the record keeps *Subscribers Run* = No and the next poll runs the subscribers again.
To retry a mail that ended in the *Error* folder, use the action *Reprocess* on the inbound mail in NAV; moving the
mail back to the source folder in Outlook does not re-import it, because the duplicate check skips known messages.

## Read-only mode with delta query

For customers who do not want to give the app write access, *Polling Mode* = *Delta Query* together with
*After Import* = *Nothing* needs only **Mail.Read**:

* `GraphMailbox.FetchChanges(deltaLink)` calls `GET .../mailFolders/{folder}/messages/delta?changeType=created`
  with `Prefer: odata.maxpagesize`. An empty link starts a new sync, bounded by `ReceivedAfter` when it is set.
* Graph returns `@odata.nextLink` pages and finally an `@odata.deltaLink`. The add-in returns it in
  `NextDeltaLink`; NAV stores the **complete** link in the BLOB field *Delta Link* (it is far longer than 250
  characters) and passes it to the next run. If `MaxMessages` is reached first, `NextDeltaLink` is the page link to
  continue with and `MoreAvailable` is true.
* Delta is per folder. Each tracked folder needs its own link.
* Entries for mails that were deleted or moved out of the folder come back in `RemovedCount` / `GetRemovedId`.
  Entries without `receivedDateTime` (update noise) and repeated ids within one run are skipped. Graph may replay
  changes, so NAV de-duplicates exactly as in watermark mode.
* **Expired sync state**: Microsoft gives delta tokens for mail no fixed lifetime; they are evicted when an internal
  cache fills. Graph then answers HTTP 410 or a `syncStateNotFound`-type code. The add-in returns an error text that
  starts with `DeltaExpired:` and sets `GraphMailbox.DeltaExpired = true`. Codeunit 50401 then starts a new sync with
  `FetchChanges("")`, bounded by the watermark, and the duplicate check absorbs the overlap.
* With a `$filter` Graph returns at most 5,000 messages in a delta sync. Set *Import From Date* for mailboxes with
  a large backlog.

Without write access NAV cannot move mails, so the mailbox is not a visible work queue. Use the NAV list
*Graph Inbound Mails* (filter *Status* = Error) instead.

## Permissions

| What | Graph application permission | Exchange RBAC role (recommended) |
|------|------------------------------|----------------------------------|
| List, delta, get message, body, attachments, `.eml` | Mail.Read | `Application Mail.Read` |
| Mark as read/unread, move, delete, create folders | Mail.ReadWrite (includes reading) | `Application Mail.ReadWrite` |
| Sending (unchanged) | Mail.Send | `Application Mail.Send` |

* **Mail.ReadBasic is not enough.** It excludes the body and attachments.
* Assign the roles through **Exchange Online RBAC for Applications**, scoped to the polled mailbox, not as
  tenant-wide Entra permissions. Entra and Exchange grants add up: a tenant-wide `Mail.Read` consent in Entra ID
  lets the app read **every** mailbox, whatever the Exchange scope says.
* The grant script does both roles: `Grant-NavMailGraphMailboxScope.ps1 -IncludeRead` (read-only) or
  `-IncludeReadWrite` (default move mode; it includes reading). Details: [exchange-rbac.md](exchange-rbac.md).
* Use a mailbox that exists only for NAV import (for example a shared mailbox `invoices@contoso.com`). Then the app
  can read only what was sent to that address, not a person's mail.
* Permission changes take 30 minutes to 2 hours to apply (Exchange cache).
* A 403 on reading adds the hint "Reading mail and attachments needs the Mail.Read application permission (Exchange
  RBAC role 'Application Mail.Read') ..."; a 403 on mark, move, delete or folder creation adds "Marking as read,
  moving, deleting mail and creating folders needs the Mail.ReadWrite application permission (Exchange RBAC role
  'Application Mail.ReadWrite') ...". Search the error text for these prefixes.

German text for customers: [de/kundeninfo-berechtigungen.md](de/kundeninfo-berechtigungen.md).

## Scheduling with the job queue

Codeunit 50401 has `TableNo` 472 (*Job Queue Entry*) and reads *Parameter String*: a mailbox code, or blank for all
enabled mailboxes. The action **Schedule Polling** on the mailbox card creates a recurring entry through Codeunit 453
`EnqueueJobQueueEntry` (every *Polling Interval (Min.)* minutes, default 5, all weekdays). A job queue (NAS) must run
for the company. Details and the manual alternative: [../nav/README.md](../nav/README.md#setup-walkthrough-reading-a-mailbox).

Error behaviour, because the NAV 2016 job queue stops a recurring entry after *Maximum No. of Attempts to Run*
failures:

* **Mailbox-level errors** (token, permission, folder not found) are saved in *Last Error* and raised with `ERROR`,
  so they appear in the job queue log (Table 474). After the attempts are used up the entry has *Status* = Error and
  must be set to Ready again once the cause is fixed.
* **Errors of single mails** do not stop the run. The mail gets *Status* = Error with the error text, moves to the
  error folder (unless *After Import* = *Nothing*) and the next mail is processed. Watch *Mails with Errors* on the
  mailbox card.

Polling every few minutes is fine for Graph (see the limits below). Several mailboxes can share one entry with a
blank *Parameter String*; a slow mailbox then delays the others.

## Events for partners

Codeunit 50401 publishes three integration events. Subscribe in your own codeunit, never change ours.

| Event | When | Transaction |
|-------|------|-------------|
| `OnBeforeImportMail(MailboxSetup : Record 50400;MailItem : DotNet NavMailGraph.GraphMailItem;VAR Skip : Boolean)` | before a mail is imported; `Skip := TRUE` ignores it | import transaction |
| `OnAfterImportAttachment(VAR InboundAttachment : Record 50402)` | after each attachment was stored | import transaction; an `ERROR` fails the import of the mail |
| `OnAfterImportMail(VAR InboundMail : Record 50401)` | after the mail and its attachments were committed | own transaction; an `ERROR` sets *Status* = Error and only your changes are rolled back |

Rules: subscribers run in the job queue (no `CONFIRM`, `MESSAGE` or `PAGE.RUN`). In `OnAfterImportMail` change the
VAR record (*Status*, *Processed At*, *Related Record ID*) but do **not** `MODIFY` it; the runner saves it. Read
attachment content with Codeunit 50403 `GetAttachmentContent(InboundAttachment,TempBlob)` regardless of whether it
sits in a BLOB, in object storage or in a server folder. Subscribers of one event run in random order in NAV 2016.

The complete purchase-invoice example is in [../nav/README.md](../nav/README.md#events-and-a-subscriber-example).
A smaller one that skips auto-replies and refuses executable attachments:

```
OBJECT Codeunit 50411 Graph Mail Import Filter
{
  OBJECT-PROPERTIES
  {
    Date=12.10.26;
    Time=12:00:00;
    Version List=;
  }
  PROPERTIES
  {
  }
  CODE
  {
    VAR
      BlockedFileErr@1000 : TextConst 'DEU=Anhang %1 ist nicht erlaubt.;ENU=Attachment %1 is not allowed.';

    [EventSubscriber(Codeunit,50401,OnBeforeImportMail)]
    PROCEDURE SkipAutoReplies@1(MailboxSetup@1000 : Record 50400;MailItem@1001 : DotNet "'NavMailGraph, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.NavMailGraph.GraphMailItem";VAR Skip@1002 : Boolean);
    BEGIN
      IF MailboxSetup.Code <> 'INVOICES' THEN
        EXIT;
      IF STRPOS(LOWERCASE(MailItem.Subject),'automatic reply') = 1 THEN
        Skip := TRUE;
    END;

    [EventSubscriber(Codeunit,50401,OnAfterImportAttachment)]
    PROCEDURE RefuseExecutables@2(VAR InboundAttachment@1000 : Record 50402);
    BEGIN
      IF InboundAttachment."Mailbox Code" <> 'INVOICES' THEN
        EXIT;
      IF STRPOS(LOWERCASE(InboundAttachment."File Name"),'.exe') > 0 THEN
        ERROR(BlockedFileErr,InboundAttachment."File Name");
    END;

    BEGIN
    END.
  }
}
```

Like the NAV objects, this example is not compiled in CI. The DotNet parameter of `OnBeforeImportMail` must match
the publisher exactly; if C/SIDE rejects a DotNet parameter in an integration event, see the known limitations in
[../nav/README.md](../nav/README.md#known-limitations--to-verify-in-cside).

## Limits and throttling

| Limit | Value | Effect |
|-------|-------|--------|
| Outlook requests per app and mailbox | 10,000 per 10 minutes, **4 concurrent** | the add-in allows at most 4 concurrent requests per mailbox per NAV Server process; a request that gets no slot within the timeout fails with a transport error |
| Upload volume per app and mailbox | 150 MB per 5 minutes | counts PATCH/POST/PUT, i.e. sending large attachments, not downloads |
| HTTP 429 / 503 | throttled / temporarily unavailable | retried with `Retry-After` (up to *MaxRetries*, default 3) |
| `$top` on list | 1–1000; the add-in pages with at most 50 | large pages with full bodies can cause HTTP 504 at Graph |
| Delta with `$filter` | at most 5,000 messages | bound the first sync with *Import From Date* |
| Delta token lifetime | not fixed (cache eviction) | handled as `DeltaExpired`, see above |
| Exchange Online message size | 36 MB receive by default, admins can raise it to 150 MB | the largest attachment NAV will ever see |

Attachment metadata is listed with `$select=id,name,contentType,size,isInline,lastModifiedDateTime` (never
`contentBytes`); content is always streamed through `.../attachments/{id}/$value`. Graph documents no size limit for
`$value` downloads. See [large-attachments.md](large-attachments.md#receiving).

## Using the add-in directly

All methods return `""` on success or an error text and never throw for Graph, network or validation problems
(only a `null` connection or storage argument throws `ArgumentNullException`). The sequence below is what
Codeunit 50401 does, written in C# for brevity:

```csharp
var connection = new GraphConnection("contoso.onmicrosoft.com", "<client id>");
connection.UseCertificateThumbprint("<thumbprint>");          // or UseClientSecret / UseCertificateFile
var mailbox = new GraphMailbox(connection, "invoices@contoso.com");
mailbox.Folder = "Inbox";
mailbox.ReceivedAfter = lastReceivedText;                     // "" on the first run
string error = mailbox.Fetch();                               // or FetchChanges(storedDeltaLink)
if (error != "") { /* log error and mailbox.LastDiagnostics, stop */ }

for (int i = 0; i < mailbox.Count; i++)
{
    GraphMailItem mail = mailbox.GetMessage(i);
    if (AlreadyImported(mail.Id, mail.InternetMessageId)) continue;
    for (int a = 0; a < mail.AttachmentCount; a++)            // -1: metadata failed, see mail.AttachmentError
    {
        GraphMailAttachmentInfo info = mail.GetAttachment(a);
        error = mailbox.SaveAttachmentToFile(mail.Id, info.Id, Path.Combine(folder, info.SuggestedFileName));
    }
    // commit the import, then:
    error = mailbox.MoveToFolder(mail.Id, "Inbox/Processed"); // creates the folder when missing
    lastReceivedText = mail.ReceivedText;
}
```

Other members: `MarkAsRead`, `MarkAsUnread`, `DeleteMessage`, `EnsureFolder` (+ `LastFolderId`),
`SaveMimeToFile` (.eml), `SaveAttachmentToStream` (e.g. a NAV BLOB OutStream), `CopyAttachmentToStorage`
([large-attachments.md](large-attachments.md#receiving)), `LastRequestId`, `LastDiagnostics`. `BodyAsText = true`
asks Graph to convert HTML bodies to text. `GraphMailItem` exposes `ReceivedText`/`SentText` (authoritative ISO
text) and `ReceivedUtc`/`SentUtc` (DateTime, UTC) for convenience.

To check permissions and folders before setting up NAV, use the CLI with the same library: `nmg inbox list`,
`get`, `save-attachment`, `move`, `delta` ([development-macos.md](development-macos.md#reading-mail-and-object-storage-v02)).
