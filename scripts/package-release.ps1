param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
    $Version = [string]$props.Project.PropertyGroup.Version
}

$publishScript = Join-Path $PSScriptRoot "publish.ps1"
& $publishScript -Configuration $Configuration -Runtime $Runtime
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$publishDirectory = Join-Path $root "artifacts\publish\$Runtime"
$releaseDirectory = Join-Path $root "artifacts\release"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

$name = "OpenUninstaller-$Version-$Runtime"
$zip = Join-Path $releaseDirectory "$name.zip"
$checksumFile = "$zip.sha256"
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}
if (Test-Path $checksumFile) {
    Remove-Item -LiteralPath $checksumFile -Force
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath $checksumFile -Encoding ascii

Write-Host "Release package: $zip"
Write-Host "SHA-256:         $hash"
Get-Item -LiteralPath $zip, $checksumFile | Select-Object Name, Length | Format-Table -AutoSize
