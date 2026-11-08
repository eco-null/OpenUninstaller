<#
.SYNOPSIS
Sets the product version in Directory.Build.props, the single source for every assembly, the
version shown in the app and CLI, the publish manifest, the zip and the installer.

.DESCRIPTION
Pass -Bump to increase the current version, or -Version to set an exact one. The script also turns
an "## [Unreleased]" section in CHANGELOG.md into the new version's section. It prints the new
version and changes nothing in git.

.EXAMPLE
./scripts/set-version.ps1 -Bump patch     # 1.0.10 -> 1.0.11
./scripts/set-version.ps1 -Version 1.1.0
#>
param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ([string]::IsNullOrWhiteSpace($Bump) -eq [string]::IsNullOrWhiteSpace($Version)) {
    throw "Pass either -Bump patch|minor|major or -Version X.Y.Z."
}

$propsPath = Join-Path $root "Directory.Build.props"
$props = [System.IO.File]::ReadAllText($propsPath)
$current = [regex]::Match($props, "<Version>(\d+)\.(\d+)\.(\d+)</Version>")
if (-not $current.Success) {
    throw "Directory.Build.props has no <Version>X.Y.Z</Version>."
}

if ($Bump) {
    $major = [int]$current.Groups[1].Value
    $minor = [int]$current.Groups[2].Value
    $patch = [int]$current.Groups[3].Value
    switch ($Bump) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    $Version = "$major.$minor.$patch"
}

if ($Version -notmatch "^\d+\.\d+\.\d+$") {
    throw "Version '$Version' is not in X.Y.Z form."
}

$props = $props -replace "<Version>[^<]*</Version>", "<Version>$Version</Version>"
$props = $props -replace "<AssemblyVersion>[^<]*</AssemblyVersion>", "<AssemblyVersion>$Version.0</AssemblyVersion>"
$props = $props -replace "<FileVersion>[^<]*</FileVersion>", "<FileVersion>$Version.0</FileVersion>"
[System.IO.File]::WriteAllText($propsPath, $props, $utf8)

$changelogPath = Join-Path $root "CHANGELOG.md"
if (Test-Path -LiteralPath $changelogPath) {
    $changelog = [System.IO.File]::ReadAllText($changelogPath)
    $unreleased = [regex]::new("(?m)^## \[Unreleased\][^\r\n]*\r?\n")
    if ($unreleased.IsMatch($changelog) -and $changelog -notmatch "(?m)^## \[$([regex]::Escape($Version))\]") {
        $date = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
        $heading = "## [Unreleased]`n`n## [$Version] $([char]0x2014) $date`n"
        $changelog = $unreleased.Replace($changelog, $heading, 1)
        [System.IO.File]::WriteAllText($changelogPath, $changelog, $utf8)
    }
}

Write-Output $Version
