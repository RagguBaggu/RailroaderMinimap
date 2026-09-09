<#
.SYNOPSIS
    Extracts one version's section from CHANGELOG.md, for use as a GitHub
    Release body.

.DESCRIPTION
    CHANGELOG.md follows the Keep a Changelog convention: each version gets
    its own "## [X.Y.Z] - date" heading, with everything until the next such
    heading (or end of file) belonging to it. This pulls just that section
    (heading line itself excluded, since the release title already shows
    the version) so the release workflow can use it as the release body.

.PARAMETER Version
    The version to extract, without a leading "v" (e.g. "0.9.0").

.PARAMETER ChangelogPath
    Path to CHANGELOG.md. Defaults to the repo root.

.PARAMETER OutFile
    Where to write the extracted section.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ChangelogPath = "CHANGELOG.md",

    [Parameter(Mandatory = $true)]
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ChangelogPath)) {
    throw "Changelog not found at '$ChangelogPath'"
}

# Windows PowerShell 5.1's Get-Content/Set-Content default to the system
# ANSI codepage, not UTF-8 -- reading this file (em dashes, arrows) with
# that default mangles them into mojibake. Reading/writing via
# System.IO.File with an explicit UTF8Encoding(false) (no BOM) avoids that
# entirely, and keeps the release body byte-identical to CHANGELOG.md's own
# encoding.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$fullChangelogPath = (Resolve-Path $ChangelogPath).Path
$lines = [System.IO.File]::ReadAllLines($fullChangelogPath, [System.Text.Encoding]::UTF8)
$escapedVersion = [regex]::Escape($Version)
$headingPattern = "^## \[$escapedVersion\]"

$startIndex = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $headingPattern) {
        $startIndex = $i
        break
    }
}

if ($startIndex -eq -1) {
    throw "No CHANGELOG.md section found for version '$Version' (looked for a line matching '$headingPattern'). Add one before tagging a release."
}

$endIndex = $lines.Count - 1
for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[') {
        $endIndex = $i - 1
        break
    }
}

$sectionLines = if ($endIndex -ge ($startIndex + 1)) { $lines[($startIndex + 1)..$endIndex] } else { @() }
$section = ($sectionLines -join "`n").Trim()

if ([string]::IsNullOrWhiteSpace($section)) {
    throw "CHANGELOG.md section for version '$Version' is empty."
}

[System.IO.File]::WriteAllText($OutFile, $section, $utf8NoBom)
Write-Host "Wrote changelog section for $Version to $OutFile"
