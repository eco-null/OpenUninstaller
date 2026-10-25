param(
    [Parameter(Mandatory = $true)]
    [string]$Thumbprint,
    [string[]]$Path = @(),
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$Signtool = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($Path.Count -eq 0) {
    $Path = @(
        (Join-Path $root "artifacts\publish\win-x64\OpenUninstaller.exe"),
        (Join-Path $root "artifacts\publish\win-x64\cli\oucli.exe"),
        (Join-Path $root "artifacts\publish\win-x64\service\OpenUninstaller.Service.exe")
    )
}

if ([string]::IsNullOrWhiteSpace($Signtool)) {
    $kits = @()
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not [string]::IsNullOrWhiteSpace($base)) {
            $kits += Join-Path $base "Windows Kits\10\bin"
        }
    }
    $kits = $kits | Where-Object { Test-Path $_ }

    $Signtool = foreach ($kit in $kits) {
        Get-ChildItem -Path $kit -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
    $Signtool = $Signtool | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Signtool) -or -not (Test-Path $Signtool)) {
    throw "signtool.exe was not found. Install the Windows SDK signing tools or pass -Signtool."
}

$normalized = $Thumbprint -replace "[^0-9A-Fa-f]", ""
if ($normalized.Length -ne 40) {
    throw "The certificate thumbprint must be 40 hexadecimal characters."
}

foreach ($file in $Path) {
    if (-not (Test-Path $file)) {
        throw "Cannot sign a missing file: $file"
    }

    Write-Host "Signing $file"
    & $Signtool sign /sha1 $normalized /fd SHA256 /tr $TimestampUrl /td SHA256 $file
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $file with exit code $LASTEXITCODE."
    }

    & $Signtool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) {
        throw "Signature verification failed for $file."
    }
}

Write-Host "Signed and verified $($Path.Count) file(s)."
