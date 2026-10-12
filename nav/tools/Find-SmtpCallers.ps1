<#
.SYNOPSIS
    Lists NAV objects in C/AL text exports that use Codeunit 400 "SMTP Mail", Table 409 "SMTP Mail Setup"
    or the SMTP DotNet types, so a partner can check a customer-modified database before applying the
    nav2016-mailgraph patches.

.DESCRIPTION
    Scans *.txt files (one object per file or one big "export all" file) for:
      Codeunit400     - "Codeunit 400" declarations or CODEUNIT::"SMTP Mail"
      SmtpServerField - direct use of the field "SMTP Server" (decides "mail possible" without Codeunit 400)
      Table409        - "Record 409" or "SMTP Mail Setup" references
      GetSMTPMessage  - calls of Codeunit 400.GetSMTPMessage (SMTP message object is not available with Graph)
      SmtpDotNet      - the DotNet type Microsoft.Dynamics.Nav.SMTP (bypasses Codeunit 400 entirely)
    Objects that the nav2016-mailgraph patches already handle (Table 409, Page 409, Codeunit 400, and our
    Codeunit 50400) and the standard users that need no change (Codeunit 412, Codeunit 9520, Page 410) are
    marked as Known. Use -ExcludeKnown to hide them.

    The script only reads files. It does not need NAV or C/SIDE; it runs on Windows PowerShell 5.1 and
    PowerShell 7 (Windows, macOS, Linux). Files are read as Latin-1 so every byte maps to a character; all
    search patterns are ASCII, so the code page of the export (OEM or Windows-1252) does not matter.

.PARAMETER Path
    Folder (searched recursively) or single .txt file.

.PARAMETER ExcludeKnown
    Hide objects that are handled by the patches or known not to need changes.

.PARAMETER CsvPath
    Optional path of a CSV file to write the results to.

.EXAMPLE
    ./Find-SmtpCallers.ps1 -Path C:\Export\Objects

.EXAMPLE
    ./Find-SmtpCallers.ps1 -Path .\all-objects.txt -ExcludeKnown -CsvPath .\smtp-usage.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Path,

    [switch] $ExcludeKnown,

    [string] $CsvPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$patterns = [ordered]@{
    Codeunit400     = '(?i)\bCodeunit\s+400\b|CODEUNIT::"SMTP Mail"'
    GetSMTPMessage  = '(?i)\.GetSMTPMessage\s*\('
    SmtpServerField = '(?i)"SMTP Server"'
    Table409        = '(?i)\bRecord\s+409\b|"SMTP Mail Setup"'
    SmtpDotNet      = '(?i)Microsoft\.Dynamics\.Nav\.SMTP\b'
}

# Objects handled by the patches or known to need no change (NAV 2016 W1 CU1).
$known = @{
    'Table 409'      = 'patched (TAB409.md)'
    'Page 409'       = 'patched (PAG409.md)'
    'Codeunit 400'   = 'patched (COD400.md)'
    'Codeunit 50400' = 'nav2016-mailgraph'
    'Codeunit 412'   = 'standard test mail, works through Codeunit 400'
    'Codeunit 9520'  = 'standard, uses Codeunit 400 IsEnabled/TrySend'
    'Page 410'       = 'standard, uses Codeunit 400 address validation'
}

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Path not found: $Path"
}

$item = Get-Item -LiteralPath $Path
if ($item.PSIsContainer) {
    $files = @(Get-ChildItem -LiteralPath $item.FullName -Recurse -File | Where-Object { $_.Extension -ieq '.txt' })
}
else {
    $files = @($item)
}

if ($files.Count -eq 0) {
    Write-Warning "No .txt files found under $Path"
    return
}

$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$objectHeader = [regex]'^OBJECT\s+(Table|Page|Report|Codeunit|Query|XMLport|MenuSuite)\s+(\d+)\s+(.*)$'
$results = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    $currentObject = $null
    $currentName = ''
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadLines($file.FullName, $latin1)) {
        $lineNo++
        $header = $objectHeader.Match($line)
        if ($header.Success) {
            $currentObject = $header.Groups[1].Value + ' ' + $header.Groups[2].Value
            $currentName = $header.Groups[3].Value.Trim()
            continue
        }
        foreach ($kind in $patterns.Keys) {
            if ($line -match $patterns[$kind]) {
                $objectKey = if ($null -ne $currentObject) { $currentObject } else { '(unknown object)' }
                $note = if ($known.ContainsKey($objectKey)) { $known[$objectKey] } else { '' }
                $results.Add([pscustomobject]@{
                    Object = $objectKey
                    Name   = $currentName
                    Kind   = $kind
                    Known  = ($note -ne '')
                    Note   = $note
                    File   = $file.FullName
                    Line   = $lineNo
                    Text   = $line.Trim()
                })
            }
        }
    }
}

$hits = $results.ToArray()
if ($ExcludeKnown) {
    $hits = @($hits | Where-Object { -not $_.Known })
}

if ($hits.Count -eq 0) {
    Write-Host 'No references found.'
    return
}

# Summary per object.
$hits |
    Group-Object Object |
    ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            Object = $_.Name
            Name   = $first.Name
            Kinds  = (($_.Group | Select-Object -ExpandProperty Kind -Unique) -join ', ')
            Hits   = $_.Count
            Known  = $first.Known
            Note   = $first.Note
        }
    } |
    Sort-Object Known, Object |
    Format-Table -AutoSize |
    Out-String -Width 200 |
    Write-Host

if ($CsvPath) {
    $hits | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "Details written to $CsvPath"
}
else {
    $hits
}
