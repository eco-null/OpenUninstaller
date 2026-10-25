param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
    $Version = [string]$props.Project.PropertyGroup.Version
}

foreach ($required in @("installer\OpenUninstaller.iss", "docs\INSTALLER.md", "LICENSE")) {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Missing installer input: $required"
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @()
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($base)) {
            $candidates += Join-Path $base "Inno Setup 6\ISCC.exe"
        }
    }
    $candidates = $candidates | Where-Object { Test-Path $_ }

    $InnoCompiler = $candidates | Select-Object -First 1
    if (-not $InnoCompiler) {
        $onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($onPath) { $InnoCompiler = $onPath.Source }
    }
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path $InnoCompiler)) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php or pass -InnoCompiler."
}

& (Join-Path $PSScriptRoot "publish.ps1") -Configuration $Configuration -Runtime $Runtime
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$source = Join-Path $root "artifacts\publish\$Runtime"
$script = Join-Path $root "installer\OpenUninstaller.iss"
Write-Host "Compiling installer $Version from $source"
& $InnoCompiler "/DMyAppVersion=$Version" "/DMyAppSource=$source" "/Qp" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $root "artifacts\release\OpenUninstaller-$Version-setup.exe"
if (-not (Test-Path $installer)) {
    throw "The installer was not produced at $installer."
}

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $installer)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii

Write-Host "Installer: $installer"
Write-Host "SHA-256:   $hash"
Get-Item -LiteralPath $installer, "$installer.sha256" | Select-Object Name, Length | Format-Table -AutoSize
