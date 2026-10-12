#!/usr/bin/env python3
"""Static checks for the nav2016-mailgraph C/AL text objects (NAV 2016 TXT exports).

This is NOT a C/AL compiler. It catches the mistakes a compiler would report first:
  * file format: CP850 decodable, CRLF only, no tabs
  * BEGIN/CASE ... END and REPEAT ... UNTIL balance, quote parity per line, brace balance
  * unique field numbers/names, control/action ids, procedure ids, variable ids per scope
  * name lengths <= 30 (objects, fields, variables, procedures)
  * every Record field used (Var."Field", Var.Field, SETRANGE/SETCURRENTKEY/... arguments, options ::Value,
    SourceExpr, SubPageLink/RunPageLink, CalcFormula, unqualified Rec fields) exists in the table definition
  * SETCURRENTKEY matches a key of the table
  * calls into our codeunits / known standard codeunits exist, are not LOCAL and have the right argument count
  * every NavMailGraph DotNet member used exists in the v0.2 API (docs/DESIGN-v2.md sections 2 and 5)
    with the documented argument count

Usage: python3 nav/tools/check_cal.py [nav/objects] [nav/patches/TAB409.md]
Exit code 1 when any check fails.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OBJ_DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "objects")
TAB409_MD = sys.argv[2] if len(sys.argv) > 2 else os.path.join(HERE, "..", "patches", "TAB409.md")

errors = []
warnings = []


def err(obj, msg):
    errors.append("%s: %s" % (obj, msg))


# --------------------------------------------------------------------------------------------- DotNet API
# name -> argument count (None = property). Constructors are listed under the type name.
DOTNET_API = {
    "GraphConnection": {
        "GraphConnection": 2, "UseClientSecret": 1, "UseCertificateThumbprint": 1, "UseCertificateFile": 2,
        "UseTokenProvider": 1, "TokenLibrary": None, "Cloud": None, "GraphBaseUrl": None, "AuthorityHost": None,
        "TimeoutSeconds": None, "MaxRetries": None, "LogFilePath": None, "TestConnection": 0,
    },
    "GraphMailbox": {
        "GraphMailbox": 2, "Folder": None, "UnreadOnly": None, "ReceivedAfter": None, "MaxMessages": None,
        "IncludeBody": None, "BodyAsText": None, "Fetch": 0, "FetchChanges": 1, "NextDeltaLink": None,
        "DeltaExpired": None, "Count": None, "GetMessage": 1, "RemovedCount": None, "GetRemovedId": 1,
        "MarkAsRead": 1, "MarkAsUnread": 1, "MoveToFolder": 2, "LastMovedMessageId": None, "EnsureFolder": 1,
        "LastFolderId": None, "DeleteMessage": 1, "SaveMimeToFile": 2, "SaveAttachmentToFile": 3,
        "SaveAttachmentToStream": 3, "CopyAttachmentToStorage": 4, "LastStorageKey": None,
        "LastStorageSize": None, "LastStorageETag": None, "LastRequestId": None, "LastDiagnostics": None,
    },
    "GraphMailItem": {
        "Id": None, "InternetMessageId": None, "ConversationId": None, "Subject": None, "FromAddress": None,
        "FromName": None, "SenderAddress": None, "ToRecipients": None, "CcRecipients": None, "ReplyTo": None,
        "ReceivedUtc": None, "ReceivedText": None, "SentUtc": None, "SentText": None, "IsRead": None,
        "Importance": None, "HasAttachments": None, "Body": None, "BodyIsHtml": None, "BodyPreview": None,
        "WebLink": None, "AttachmentCount": None, "GetAttachment": 1, "LoadAttachments": 0, "AttachmentError": None,
    },
    "GraphMailAttachmentInfo": {
        "Id": None, "Name": None, "ContentType": None, "Size": None, "IsInline": None, "ContentId": None,
        "Kind": None, "SuggestedFileName": None,
    },
    "ObjectStorageClient": {
        "ObjectStorageClient": 0, "UseAzureBlobSharedKey": 3, "UseAzureBlobSasUrl": 1, "UseAzureBlobEntra": 3,
        "UseS3": 5, "BlobEndpointSuffix": None, "ServiceUrl": None, "S3PathStyle": None, "S3SessionToken": None,
        "KeyPrefix": None, "MultipartThresholdBytes": None, "PartSizeBytes": None, "TimeoutSeconds": None,
        "MaxRetries": None, "LogFilePath": None, "AzureApiVersion": None, "UploadFile": 3, "UploadStream": 3,
        "UploadText": 3, "DownloadToFile": 2, "DownloadToStream": 2, "Delete": 1, "Exists": 1, "GetSize": 1,
        "List": 1, "ListCount": None, "GetListedKey": 1, "GetListedSize": 1, "GetDownloadUrl": 2,
        "TestConnection": 0, "LastError": None, "LastDiagnostics": None, "Describe": 0,
    },
    # v1 members (src/NavMailGraph, existing) + v2 additions (UseConnection, UseLinkStorage, ClearLinkStorage)
    "GraphMailSender": {
        "GraphMailSender": 2, "UseClientSecret": 1, "UseCertificateThumbprint": 1, "UseCertificateFile": 2,
        "UseTokenProvider": 1, "TokenLibrary": None, "Cloud": None, "GraphBaseUrl": None, "AuthorityHost": None,
        "SenderMailbox": None, "ReplyToFromAddress": None, "UseDraftForLargeMessages": None,
        "SaveToSentItems": None, "SendX5C": None, "TimeoutSeconds": None, "MaxRetries": None,
        "SendMailPayloadLimitBytes": None, "DirectAttachmentLimitBytes": None, "UploadChunkBytes": None,
        "MaxAttachmentBytes": None, "LogFilePath": None, "Send": 1, "TestConnection": 0, "LastMessageId": None,
        "LastRequestId": None, "LastDiagnostics": None, "Version": None,
        "UseConnection": 1, "UseLinkStorage": 4, "ClearLinkStorage": 0,
    },
    "GraphMailMessage": {
        "GraphMailMessage": 0, "FromName": None, "FromAddress": None, "Subject": None, "Body": None,
        "HtmlFormatted": None, "Importance": None, "AddRecipients": 1, "AddCC": 1, "AddBCC": 1, "AddReplyTo": 1,
        "AppendBody": 1, "AddAttachment": 2, "AddAttachmentBytes": 2, "AddAttachmentStream": 2,
        "AddInlineAttachment": 3, "AddHeader": 2, "RecipientCount": None, "AttachmentCount": None,
        "TotalAttachmentBytes": None, "GetBody": 0, "GetSubject": 0, "Describe": 0,
        "AddAttachmentFromStorage": 3,
    },
}
SYSTEM_DOTNET = {
    "System.Text.RegularExpressions.Regex": {"Replace": 3},
    "System.Net.WebUtility": {"HtmlDecode": 1},
}

# --------------------------------------------------------------------------------------------- standard objects
TABLE_472_FIELDS = [
    "ID", "User ID", "XML", "Last Ready State", "Expiration Date/Time", "Earliest Start Date/Time",
    "Object Type to Run", "Object ID to Run", "Object Caption to Run", "Report Output Type",
    "Maximum No. of Attempts to Run", "Status", "Priority", "Record ID to Process", "Parameter String",
    "Recurring Job", "No. of Minutes between Runs", "Run on Mondays", "Run on Tuesdays", "Run on Wednesdays",
    "Run on Thursdays", "Run on Fridays", "Run on Saturdays", "Run on Sundays", "Starting Time", "Ending Time",
    "Reference Starting Time", "Description", "Run in User Session", "User Session ID", "Job Queue Category Code",
    "Error Message", "Error Message 2", "Error Message 3", "Error Message 4", "User Service Instance ID",
    "User Session Started", "Timeout (sec.)", "Notify On Success", "User Language ID", "Printer Name",
    "Report Request Page Options", "Rerun Delay (sec.)",
]
STD_TABLES = {
    472: {"name": "Job Queue Entry", "fields": {f: None for f in TABLE_472_FIELDS},
          "procs": {"SetStatus": 1, "GetErrorMessage": 0, "SetError": 1, "Cancel": 0, "Restart": 0},
          "keys": [["ID"], ["Priority", "Last Ready State"]]},
    1261: {"name": "Service Password", "fields": {"Key": None, "Value": None},
           "procs": {"SavePassword": 1, "GetPassword": 0}, "keys": [["Key"]]},
    # NAV 2016 virtual table (Table 472 SessionExists / Codeunit 448 UserSessionExists filter it this way)
    2000000110: {"name": "Active Session", "fields": {"Server Instance ID": None, "Session ID": None,
                                                       "Client Type": None, "Login Datetime": None},
                 "procs": {}, "keys": [["Server Instance ID", "Session ID"]]},
    99008535: {"name": "TempBlob", "fields": {"Primary Key": None, "Blob": None},
               "procs": {"ReadAsText": 2}, "keys": [["Primary Key"]]},
}
STD_TABLES[472]["fields"]["Object Type to Run"] = ["", "", "", "Report", "", "Codeunit"]
STD_TABLES[472]["fields"]["Status"] = ["Ready", "In Process", "Error", "On Hold", "Finished"]
# Standard codeunits used: procedure -> argument count (NAV 2016 CU1 exports, see research notes)
STD_CODEUNITS = {
    419: {"BLOBExport": 3, "ServerTempFileName": 1, "BLOBImportFromServerFile": 2, "BLOBExportToServerFile": 2,
          "DeleteServerFile": 1, "ServerCreateDirectory": 1, "GetDirectoryName": 1,
          "StripNotsupportChrInFileName": 1, "GetFileName": 1, "GetExtension": 1},
    453: {"EnqueueJobQueueEntry": 2},
    9520: {"CheckValidEmailAddresses": 1},
}
STD_PAGES = {672, 9905}
TABLE_409_STD = ["Primary Key", "SMTP Server", "Authentication", "User ID", "Password", "SMTP Server Port",
                 "Secure Connection"]

KEYWORDS = {"IF", "THEN", "ELSE", "BEGIN", "END", "CASE", "OF", "REPEAT", "UNTIL", "WHILE", "DO", "FOR", "TO",
            "DOWNTO", "EXIT", "AND", "OR", "NOT", "XOR", "DIV", "MOD", "IN", "WITH", "VAR", "TRUE", "FALSE",
            "PROCEDURE", "LOCAL", "EXIT"}


# --------------------------------------------------------------------------------------------- helpers
def strip_strings(line):
    """Replace the content of '...' strings with spaces (C/AL strings cannot span lines)."""
    out, i, n = [], 0, len(line)
    while i < n:
        c = line[i]
        if c == "'":
            j = i + 1
            while j < n:
                if line[j] == "'":
                    if j + 1 < n and line[j + 1] == "'":
                        j += 2
                        continue
                    break
                j += 1
            out.append("'" + " " * (j - i - 1) + ("'" if j < n else ""))
            i = j + 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def strip_comment(line):
    s = strip_strings(line)
    k = s.find("//")
    return line[:k] if k >= 0 else line


def split_args(text):
    """Split the argument text (without outer parentheses) at top-level commas."""
    args, depth, cur, i, inq, indq = [], 0, "", 0, False, False
    while i < len(text):
        c = text[i]
        if c == "'" and not indq:
            inq = not inq
        elif c == '"' and not inq:
            indq = not indq
        elif not inq and not indq:
            if c in "([":
                depth += 1
            elif c in ")]":
                depth -= 1
            elif c == "," and depth == 0:
                args.append(cur)
                cur = ""
                i += 1
                continue
        cur += c
        i += 1
    if cur.strip():
        args.append(cur)
    return [a.strip() for a in args]


def call_args(code, pos):
    """code[pos] == '(' -> (argument list, end index)."""
    depth, i, inq, indq = 0, pos, False, False
    while i < len(code):
        c = code[i]
        if c == "'" and not indq:
            inq = not inq
        elif c == '"' and not inq:
            indq = not indq
        elif not inq and not indq:
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth == 0:
                    return split_args(code[pos + 1:i]), i
        i += 1
    return None, pos


def parse_type(t):
    t = t.strip().rstrip(";").strip()
    m = re.match(r"(TEMPORARY\s+)?Record\s+(\d+)", t)
    if m:
        return ("Record", int(m.group(2)))
    m = re.match(r"Codeunit\s+(\d+)", t)
    if m:
        return ("Codeunit", int(m.group(1)))
    m = re.match(r'DotNet\s+"\'[^\']*\'\.([\w.]+)"', t)
    if m:
        return ("DotNet", m.group(1))
    m = re.match(r"TextConst\b", t)
    if m:
        return ("TextConst", None)
    return (t.split()[0] if t else "", None)


# --------------------------------------------------------------------------------------------- parse objects
def read_obj(path):
    raw = open(path, "rb").read()
    name = os.path.basename(path)
    try:
        text = raw.decode("cp850")
    except UnicodeDecodeError as e:
        err(name, "not CP850: %s" % e)
        text = raw.decode("cp850", "replace")
    if b"\n" in raw.replace(b"\r\n", b""):
        err(name, "line ending other than CRLF found")
    if b"\t" in raw:
        err(name, "tab character found")
    if raw[:3] == b"\xef\xbb\xbf":
        err(name, "UTF-8 BOM found")
    return text.replace("\r\n", "\n")


def section(text, title):
    """Return the body lines of '  TITLE\n  {\n ... \n  }' (2-space indented sections)."""
    m = re.search(r"^  %s\n  \{\n(.*?)^  \}\n" % re.escape(title), text, re.S | re.M)
    return m.group(1) if m else ""


def parse_table_fields(body, objname):
    fields, order = {}, []
    blocks = re.split(r"\n(?=    \{ \d)", "\n" + body)
    for b in blocks:
        b = b.strip("\n")
        if not b.strip():
            continue
        m = re.match(r"    \{ (\d+)\s*;[^;]*;([^;]+?)\s*;(\w+)\s*;", b)
        if not m:
            err(objname, "cannot parse field line: %s" % b.splitlines()[0])
            continue
        no, fname, ftype = int(m.group(1)), m.group(2).rstrip(), m.group(3)
        opts = None
        om = re.search(r"OptionString=(\[[^\]]*\]|[^;}\n]*)", b)
        if om:
            v = om.group(1).strip()
            if v.startswith("["):
                v = v[1:-1]
            opts = v.split(",")
        cm = re.search(r"CalcFormula=(.*?\)\));", b, re.S)
        fields[fname] = {"no": no, "type": ftype, "options": opts, "block": b,
                         "calc": re.sub(r"\s+", " ", cm.group(1)) if cm else None}
        order.append((no, fname))
    return fields, order


def parse_code(text, objname):
    """Return (globals, procedures, triggers). procedures: name -> dict(id, local, params, vars, code)."""
    code = section(text, "CODE")
    globs, procs = {}, {}
    lines = code.split("\n")
    i = 0
    # globals
    if i < len(lines) and lines[i].strip() == "VAR":
        i += 1
        while i < len(lines) and re.match(r"      \w+@\d+ :", lines[i]):
            m = re.match(r"      (\w+)@(\d+) : (.*?);?$", lines[i])
            globs[m.group(1)] = {"id": int(m.group(2)), "type": parse_type(m.group(3)), "decl": m.group(3)}
            i += 1
    cur = None
    attrs = []
    while i < len(lines):
        ln = lines[i]
        s = ln.strip()
        if re.match(r"    \[.*\]$", ln):
            attrs.append(s)
        else:
            m = re.match(r"    (LOCAL )?PROCEDURE (\w+)@(\d+)\((.*)\)\s*(\w+)?\s*(?::\s*(.*?))?;$", ln)
            if m:
                params = {}
                ptext = m.group(4)
                for p in [x for x in re.split(r";(?=(?:VAR )?\w+@\d+)", ptext) if x.strip()]:
                    pm = re.match(r"(VAR )?(\w+)@(\d+) : (.*)$", p.strip())
                    if not pm:
                        err(objname, "cannot parse parameter '%s' of %s" % (p, m.group(2)))
                        continue
                    params[pm.group(2)] = {"id": int(pm.group(3)), "type": parse_type(pm.group(4)),
                                           "var": bool(pm.group(1)), "decl": pm.group(4)}
                cur = {"name": m.group(2), "id": int(m.group(3)), "local": bool(m.group(1)), "params": params,
                       "vars": {}, "code": [], "attrs": attrs, "retname": m.group(5)}
                attrs = []
                if m.group(2) in procs:
                    err(objname, "duplicate procedure name %s" % m.group(2))
                procs[m.group(2)] = cur
                i += 1
                if i < len(lines) and lines[i].strip() == "VAR":
                    i += 1
                    while i < len(lines) and re.match(r"      \w+@\d+ :", lines[i]):
                        vm = re.match(r"      (\w+)@(\d+) : (.*?);?$", lines[i])
                        cur["vars"][vm.group(1)] = {"id": int(vm.group(2)), "type": parse_type(vm.group(3))}
                        i += 1
                continue
            if cur is not None:
                if s == "BEGIN" and ln.startswith("    BEGIN") and len(ln) == 9 and i + 1 < len(lines) and \
                        lines[i + 1].strip() == "{":
                    cur = None  # documentation trigger
                else:
                    cur["code"].append(ln)
        i += 1
    return globs, procs


def parse_trigger_blocks(text):
    """Find property triggers 'OnX=VAR|BEGIN ... END;' outside CODE. Return list of (name, locals, code_lines)."""
    head = text.split("\n  CODE\n")[0]
    res = []
    lines = head.split("\n")
    i = 0
    while i < len(lines):
        m = re.match(r"^(\s*)(?:\{[^;]*;[^;]*;[^;]*;[^;]*;)?\s*(On\w+)=(VAR|BEGIN)\s*$", lines[i])
        if not m:
            m2 = re.search(r";(On\w+)=(VAR|BEGIN)\s*$", lines[i])
            if m2:
                m = m2
                name, kind = m2.group(1), m2.group(2)
                col = lines[i].index(name) + len(name) + 1
            else:
                i += 1
                continue
        else:
            name, kind = m.group(2), m.group(3)
            col = lines[i].index(name + "=") + len(name) + 1
        loc = {}
        i += 1
        if kind == "VAR":
            while i < len(lines) and lines[i].strip() != "BEGIN":
                vm = re.match(r"\s*(\w+)@(\d+) : (.*?);$", lines[i])
                if vm:
                    loc[vm.group(1)] = {"id": int(vm.group(2)), "type": parse_type(vm.group(3))}
                i += 1
            i += 1
        body = []
        while i < len(lines) and not (lines[i].startswith(" " * col + "END;") and len(lines[i]) == col + 4):
            body.append(lines[i])
            i += 1
        res.append((name, loc, body))
        i += 1
    return res


objects = {}  # (type, id) -> info


def load_objects():
    for fn in sorted(os.listdir(OBJ_DIR)):
        if not fn.upper().endswith(".TXT"):
            continue
        path = os.path.join(OBJ_DIR, fn)
        text = read_obj(path)
        m = re.match(r"OBJECT (Table|Page|Codeunit) (\d+) (.+)\n", text)
        if not m:
            err(fn, "no OBJECT header")
            continue
        otype, oid, oname = m.group(1), int(m.group(2)), m.group(3).strip()
        info = {"type": otype, "id": oid, "name": oname, "file": fn, "text": text}
        if len(oname) > 30:
            err(fn, "object name longer than 30: %s" % oname)
        props = section(text, "PROPERTIES")
        info["props"] = props
        if otype == "Table":
            info["fields"], info["order"] = parse_table_fields(section(text, "FIELDS"), fn)
            info["keys"] = [[f.strip() for f in k.split(",")]
                            for k in re.findall(r"^    \{\s*;([^;}]+?)\s*(?:;|\})", section(text, "KEYS"), re.M)]
        tm = re.search(r"TableNo=(\d+)", props)
        info["tableno"] = int(tm.group(1)) if tm else None
        sm = re.search(r"SourceTable=Table(\d+)", props)
        info["source"] = int(sm.group(1)) if sm else None
        info["globals"], info["procs"] = parse_code(text, fn)
        info["triggers"] = parse_trigger_blocks(text)
        objects[(otype, oid)] = info


def load_table409():
    md = open(TAB409_MD, encoding="utf-8").read()
    fields = {f: {"no": n, "options": None} for n, f in enumerate(TABLE_409_STD, 1)}
    for blk in re.findall(r"```\n(.*?)```", md, re.S):
        if "{ 501" not in blk:
            continue
        f2, _ = parse_table_fields(blk.rstrip("\n"), "TAB409.md")
        fields.update(f2)
    return fields


# --------------------------------------------------------------------------------------------- checks
def table_info(no):
    if ("Table", no) in objects:
        t = objects[("Table", no)]
        procs = {n: len(p["params"]) for n, p in t["procs"].items() if not p["local"]}
        return {"name": t["name"], "fields": {k: v["options"] for k, v in t["fields"].items()}, "procs": procs,
                "keys": t["keys"]}
    if no == 409:
        return {"name": "SMTP Mail Setup", "fields": {k: v["options"] for k, v in T409.items()}, "procs": {},
                "keys": [["Primary Key"]]}
    if no in STD_TABLES:
        return STD_TABLES[no]
    return None


def table_by_name(name):
    for (t, i), o in objects.items():
        if t == "Table" and o["name"] == name:
            return i
    return None


def object_by_name(otype, name):
    for (t, i), o in objects.items():
        if t == otype and o["name"] == name:
            return i
    return None


def check_field(objname, tno, fname, where):
    ti = table_info(tno)
    if ti is None:
        return
    if fname not in ti["fields"]:
        err(objname, "%s: field '%s' does not exist in table %d %s" % (where, fname, tno, ti["name"]))
        return False
    return True


def check_option(objname, tno, fname, value, where):
    ti = table_info(tno)
    if ti is None or fname not in ti["fields"]:
        return
    opts = ti["fields"][fname]
    if opts is None:
        err(objname, "%s: '%s' is not an option field (value %s)" % (where, fname, value))
        return
    v = value.strip('"')
    if v == " ":
        v = ""
    if v not in [o.strip() if o.strip() else "" for o in opts]:
        err(objname, "%s: option value '%s' not in %s.%s (%s)" % (where, value, ti["name"], fname, ",".join(opts)))


FIELD_ARG_METHODS = {"SETRANGE": 1, "SETFILTER": 1, "CALCFIELDS": None, "TESTFIELD": 1, "FIELDERROR": 1,
                     "VALIDATE": 1, "SETCURRENTKEY": None, "MODIFYALL": 1, "CALCSUMS": None}
IDENT = r'(?:"[^"]+"|\w+)'


def unq(s):
    s = s.strip()
    return s[1:-1] if s.startswith('"') and s.endswith('"') else s


def check_code_block(obj, scope_vars, code_lines, rec_table, where):
    """scope_vars: name -> {type: (...)}; rec_table: table no of implicit Rec or None."""
    objname = obj["file"]
    joined = "\n".join(strip_comment(l) for l in code_lines)
    nostr = "\n".join(strip_strings(l) for l in joined.split("\n"))
    # balance
    toks = re.findall(r"\b(BEGIN|CASE|END|REPEAT|UNTIL)\b", nostr)
    depth = rep = 0
    for t in toks:
        if t in ("BEGIN", "CASE"):
            depth += 1
        elif t == "END":
            depth -= 1
        elif t == "REPEAT":
            rep += 1
        elif t == "UNTIL":
            rep -= 1
        if depth < 0 or rep < 0:
            err(objname, "%s: END/UNTIL without opening BEGIN/CASE/REPEAT" % where)
            break
    if depth != 0:
        err(objname, "%s: BEGIN/CASE/END unbalanced (%+d)" % (where, depth))
    if rep != 0:
        err(objname, "%s: REPEAT/UNTIL unbalanced (%+d)" % (where, rep))

    def vtype(name):
        v = scope_vars.get(name)
        return v["type"] if v else None

    # Var."Field"::Option / Var.Field::Option
    for m in re.finditer(r'"[^"\n]*"|\b(\w+)\.(' + IDENT + r')::("[^"]*"|\w+)', nostr):
        if m.group(1) is None:
            continue  # quoted identifier: its content is not code
        t = vtype(m.group(1))
        if t and t[0] == "Record":
            if check_field(objname, t[1], unq(m.group(2)), where):
                check_option(objname, t[1], unq(m.group(2)), m.group(3), where)
    # unqualified "Field"::Option / Field::Option (Rec context)
    for m in re.finditer(r"(?<![.\w\"])(" + IDENT + r")::(\"[^\"]*\"|\w+)", nostr):
        name = unq(m.group(1))
        if name in ("CODEUNIT", "PAGE", "DATABASE", "REPORT", "TEXTENCODING", "XMLPORT"):
            if name in ("CODEUNIT", "PAGE"):
                otype = "Codeunit" if name == "CODEUNIT" else "Page"
                target = unq(m.group(2))
                if object_by_name(otype, target) is None:
                    err(objname, "%s: %s::%s refers to no object in nav/objects" % (where, name, m.group(2)))
            continue
        if rec_table and name not in scope_vars:
            if check_field(objname, rec_table, name, where):
                check_option(objname, rec_table, name, m.group(2), where)
    # Var."Field" and Var.Member
    arg_spans = []
    for m in re.finditer(r'"[^"\n]*"|\b(\w+)\.(' + IDENT + r')', nostr):
        if m.group(1) is None:
            continue  # quoted identifier: its content is not code
        var, member = m.group(1), m.group(2)
        t = vtype(var)
        if not t:
            continue
        after = nostr[m.end():m.end() + 1]
        if t[0] == "Record":
            mname = unq(member)
            if member.startswith('"'):
                check_field(objname, t[1], mname, where)
            elif mname.isupper() or mname in ("Rec",):
                # built-in record method: check field arguments
                if mname in FIELD_ARG_METHODS and after == "(":
                    args, _ = call_args(nostr, m.end())
                    if args is None:
                        continue
                    n = FIELD_ARG_METHODS[mname]
                    fargs = args if n is None else args[:n]
                    _, end = call_args(nostr, m.end())
                    first_comma = nostr.find(",", m.end())
                    span_end = end if n is None or first_comma < 0 or first_comma > end else first_comma
                    arg_spans.append((m.end(), span_end))
                    for a in fargs:
                        if re.fullmatch(IDENT, a):
                            check_field(objname, t[1], unq(a), where)
                    if mname == "SETCURRENTKEY":
                        ti = table_info(t[1])
                        if ti and not any(k[:len(fargs)] == [unq(a) for a in fargs] for k in ti["keys"]):
                            err(objname, "%s: SETCURRENTKEY(%s) matches no key of table %d" %
                                (where, ",".join(fargs), t[1]))
            else:
                ti = table_info(t[1])
                if ti is None:
                    continue
                if mname in ti["fields"]:
                    continue
                if mname in ti.get("procs", {}):
                    if after == "(":
                        args, _ = call_args(nostr, m.end())
                        if args is not None and len(args) != ti["procs"][mname]:
                            err(objname, "%s: %s.%s called with %d args, expects %d" %
                                (where, var, mname, len(args), ti["procs"][mname]))
                    continue
                err(objname, "%s: %s.%s is neither a field nor a function of table %d" % (where, var, mname, t[1]))
        elif t[0] == "DotNet":
            short = t[1].split(".")[-1]
            api = DOTNET_API.get(short) if t[1].startswith("NavMailGraph.") else SYSTEM_DOTNET.get(t[1])
            if api is None:
                err(objname, "%s: DotNet type %s not in the known API" % (where, t[1]))
                continue
            mname = unq(member)
            if mname not in api:
                err(objname, "%s: %s.%s is not a member of %s (DESIGN-v2)" % (where, var, mname, t[1]))
                continue
            expected = api[mname]
            if after == "(":
                args, _ = call_args(nostr, m.end())
                if expected is None:
                    err(objname, "%s: property %s.%s called like a method" % (where, var, mname))
                elif args is not None and len(args) != expected:
                    err(objname, "%s: %s.%s called with %d args, expects %d" % (where, var, mname, len(args), expected))
            elif expected not in (None, 0):
                err(objname, "%s: method %s.%s used without arguments" % (where, var, mname))
        elif t[0] == "Codeunit":
            mname = unq(member)
            if mname.isupper():
                continue  # RUN
            cu = objects.get(("Codeunit", t[1]))
            if cu:
                p = cu["procs"].get(mname)
                if not p:
                    err(objname, "%s: Codeunit %d has no procedure %s" % (where, t[1], mname))
                    continue
                if p["local"]:
                    err(objname, "%s: %s.%s is LOCAL in Codeunit %d" % (where, var, mname, t[1]))
                nexp = len(p["params"])
            elif t[1] in STD_CODEUNITS:
                if mname not in STD_CODEUNITS[t[1]]:
                    err(objname, "%s: Codeunit %d has no known procedure %s" % (where, t[1], mname))
                    continue
                nexp = STD_CODEUNITS[t[1]][mname]
            else:
                warnings.append("%s: %s: call into unknown Codeunit %d.%s not checked" % (objname, where, t[1], mname))
                continue
            if after == "(":
                args, _ = call_args(nostr, m.end())
                if args is not None and len(args) != nexp:
                    err(objname, "%s: %s.%s called with %d args, expects %d" % (where, var, mname, len(args), nexp))
            elif nexp != 0:
                err(objname, "%s: %s.%s called without arguments, expects %d" % (where, var, mname, nexp))
    # unqualified quoted identifiers (Rec fields in table/page/TableNo code)
    for m in re.finditer(r'"([^"\n]*)"', nostr):
        name = m.group(1)
        before = nostr[max(0, m.start() - 2):m.start()]
        if before.endswith("::") or before.endswith("."):
            continue
        if nostr[m.end():m.end() + 2] == "::":
            continue
        if any(a <= m.start() < b for a, b in arg_spans):
            continue
        if name in scope_vars:
            continue
        if rec_table:
            check_field(objname, rec_table, name, where)
        else:
            err(objname, "%s: quoted identifier \"%s\" outside a record context" % (where, name))
    # unqualified calls to own procedures: argument count
    procs = obj["procs"]
    for m in re.finditer(r"(?<![.\w\"])(\w+)\s*\(", nostr):
        name = m.group(1)
        if name in procs and name not in scope_vars:
            args, _ = call_args(nostr, m.end() - 1)
            if args is not None and len(args) != len(procs[name]["params"]):
                err(objname, "%s: %s called with %d args, expects %d" % (where, name, len(args),
                                                                         len(procs[name]["params"])))


def check_ids_and_names(obj):
    fn = obj["file"]
    g = obj["globals"]
    ids = {}
    for n, v in g.items():
        if len(n) > 30:
            err(fn, "variable name longer than 30: %s" % n)
        if v["id"] in ids:
            err(fn, "duplicate global variable id @%d (%s, %s)" % (v["id"], n, ids[v["id"]]))
        ids[v["id"]] = n
    pids = {}
    for n, p in obj["procs"].items():
        if len(n) > 30:
            err(fn, "procedure name longer than 30: %s" % n)
        if p["id"] in pids:
            err(fn, "duplicate procedure id @%d (%s, %s)" % (p["id"], n, pids[p["id"]]))
        pids[p["id"]] = n
        if n in g:
            err(fn, "procedure %s has the same name as a global variable" % n)
        sids = {}
        for vn, v in list(p["params"].items()) + list(p["vars"].items()):
            if len(vn) > 30:
                err(fn, "%s: variable name longer than 30: %s" % (n, vn))
            if v["id"] in sids:
                err(fn, "%s: duplicate variable id @%d (%s, %s)" % (n, v["id"], vn, sids[v["id"]]))
            sids[v["id"]] = vn
        if "[Integration]" in p["attrs"] or "[Integration(TRUE)]" in p["attrs"]:
            if [l for l in p["code"] if l.strip() and l.strip() not in ("BEGIN", "END;")]:
                err(fn, "event publisher %s must have an empty body" % n)
            if p["vars"]:
                err(fn, "event publisher %s must not have local variables" % n)
    if obj["type"] == "Table":
        nos, names = {}, set()
        for no, fname in obj["order"]:
            if no in nos:
                err(fn, "duplicate field number %d" % no)
            nos[no] = fname
            if fname in names:
                err(fn, "duplicate field name %s" % fname)
            names.add(fname)
            if len(fname) > 30:
                err(fn, "field name longer than 30: %s" % fname)
        for k in obj["keys"]:
            for f in k:
                if f not in obj["fields"]:
                    err(fn, "key field %s does not exist" % f)
        for fname, f in obj["fields"].items():
            if f["type"] == "Option" and not f["options"]:
                err(fn, "option field %s has no OptionString" % fname)
            if f["calc"]:
                cm = re.match(r'(Count|Exist|Sum|Lookup)\("([^"]+)"(?:\.("?[^"]+"?|\w+))? WHERE \((.*)\)\)$', f["calc"])
                if not cm:
                    err(fn, "cannot parse CalcFormula of %s" % fname)
                    continue
                tno = table_by_name(cm.group(2))
                if tno is None:
                    err(fn, "CalcFormula of %s: table %s not found" % (fname, cm.group(2)))
                    continue
                for cond in split_args(cm.group(4)):
                    mm = re.match(r"(.+?)=(FIELD|CONST|FILTER)\((.*)\)$", cond.strip())
                    if not mm:
                        err(fn, "CalcFormula of %s: cannot parse %s" % (fname, cond))
                        continue
                    check_field(fn, tno, mm.group(1), "CalcFormula " + fname)
                    if mm.group(2) == "FIELD":
                        check_field(fn, obj["id"], mm.group(3), "CalcFormula " + fname)
                    elif mm.group(2) == "CONST":
                        tinfo = table_info(tno)
                        if tinfo and tinfo["fields"].get(mm.group(1)):
                            check_option(fn, tno, mm.group(1), mm.group(3), "CalcFormula " + fname)
            tr = re.search(r'TableRelation="?([^";.]+)"?(?:\.(\w+))?', f["block"])
            if tr:
                tname = tr.group(1)
                if tname not in ("Service Password",) and table_by_name(tname) is None:
                    err(fn, "TableRelation of %s: table %s not found" % (fname, tname))
    if obj["type"] == "Page":
        text = obj["text"]
        cids = {}
        for m in re.finditer(r"^ {4,6}\{ (\d+)\s*;", text, re.M):
            i = int(m.group(1))
            if i in cids:
                err(fn, "duplicate control/action id %d" % i)
            cids[i] = True
        for m in re.finditer(r"^\s+Name=(\w+)", text, re.M):
            if len(m.group(1)) > 30:
                err(fn, "control name longer than 30: %s" % m.group(1))


def check_quotes_and_braces(obj):
    fn = obj["file"]
    depth = 0
    in_doc = False
    for ln_no, line in enumerate(obj["text"].split("\n"), 1):
        code = strip_comment(line)
        if code.count("'") % 2:
            err(fn, "line %d: odd number of single quotes" % ln_no)
        if strip_strings(code).count('"') % 2:
            err(fn, "line %d: odd number of double quotes" % ln_no)
        s = strip_strings(code)
        depth += s.count("{") - s.count("}")
    if depth != 0:
        err(fn, "braces unbalanced (%+d)" % depth)


def check_page(obj):
    fn = obj["file"]
    text = obj["text"]
    src = obj["source"]
    globs = obj["globals"]
    if src is None:
        err(fn, "page without SourceTable")
        return
    for m in re.finditer(r"SourceExpr=(\"[^\"]+\"|\w+)", text):
        name = unq(m.group(1))
        if name in globs:
            continue
        check_field(fn, src, name, "SourceExpr")
    for m in re.finditer(r"(SubPageLink|RunPageLink)=(.*?);\n", text, re.S):
        kind = m.group(1)
        target_tab = None
        block_end = text.find("}", m.end())
        block = text[m.start():block_end]
        pm = re.search(r"(?:PagePartID=Page|RunObject=Page )(\d+)", text[text.rfind("{", 0, m.start()):block_end])
        if pm:
            pid = int(pm.group(1))
            po = objects.get(("Page", pid))
            if po:
                target_tab = po["source"]
            elif pid not in STD_PAGES:
                err(fn, "%s: page %d not found" % (kind, pid))
        for cond in split_args(re.sub(r"\s+", " ", m.group(2))):
            mm = re.match(r"(.+?)=(FIELD|CONST|FILTER)\((.*)\)$", cond.strip())
            if not mm:
                err(fn, "%s: cannot parse %s" % (kind, cond))
                continue
            if target_tab:
                check_field(fn, target_tab, mm.group(1), kind)
            if mm.group(2) == "FIELD":
                check_field(fn, src, mm.group(3), kind)
    for m in re.finditer(r"(?:PagePartID=Page|RunObject=Page )(\d+)", text):
        pid = int(m.group(1))
        if ("Page", pid) not in objects and pid not in STD_PAGES:
            err(fn, "page %d referenced but not found" % pid)
    cm = re.search(r"CardPageID=(.+?);", text)
    if cm and object_by_name("Page", cm.group(1)) is None:
        err(fn, "CardPageID %s not found" % cm.group(1))
    for m in re.finditer(r"SourceTableView=SORTING\((.*?)\)", text):
        ti = table_info(src)
        keyf = [f.strip() for f in m.group(1).split(",")]
        if ti and not any(k[:len(keyf)] == keyf for k in ti["keys"]):
            err(fn, "SourceTableView SORTING(%s) matches no key" % m.group(1))


def main():
    global T409
    T409 = load_table409()
    load_objects()
    for key, obj in sorted(objects.items()):
        check_quotes_and_braces(obj)
        check_ids_and_names(obj)
        rec_table = obj["id"] if obj["type"] == "Table" else obj["source"] if obj["type"] == "Page" else obj["tableno"]
        for tname, loc, body in obj["triggers"]:
            scope = dict(obj["globals"])
            scope.update(loc)
            scope["Rec"] = {"type": ("Record", rec_table)} if rec_table else None
            if scope["Rec"] is None:
                del scope["Rec"]
            scope["xRec"] = scope.get("Rec")
            if scope["xRec"] is None:
                del scope["xRec"]
            check_code_block(obj, scope, body, rec_table, "trigger " + tname)
        for pname, p in obj["procs"].items():
            scope = dict(obj["globals"])
            scope.update(p["params"])
            scope.update(p["vars"])
            if rec_table:
                scope["Rec"] = {"type": ("Record", rec_table)}
            check_code_block(obj, scope, p["code"], rec_table if obj["type"] != "Codeunit" else None,
                             "procedure " + pname)
        if obj["type"] == "Page":
            check_page(obj)
    print("Objects checked: %d (%s)" % (len(objects), ", ".join(o["file"] for _, o in sorted(objects.items()))))
    print("Table 409 fields known from %s: %d" % (os.path.basename(TAB409_MD), len(T409)))
    for w in warnings:
        print("WARN  " + w)
    for e in errors:
        print("ERROR " + e)
    print("%d error(s), %d warning(s)" % (len(errors), len(warnings)))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
