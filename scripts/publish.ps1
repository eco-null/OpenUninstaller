param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "artifacts\publish\$Runtime"
}

$output = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

$common = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

Write-Host "Publishing desktop app to $output"
dotnet publish (Join-Path $root "src\OpenUninstaller.App\OpenUninstaller.App.csproj") @common -o $output
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed with exit code $LASTEXITCODE." }

$cliOutput = Join-Path $output "cli"
Write-Host "Publishing CLI to $cliOutput"
dotnet publish (Join-Path $root "src\OpenUninstaller.Cli\OpenUninstaller.Cli.csproj") @common -o $cliOutput
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE." }

$serviceOutput = Join-Path $output "service"
Write-Host "Publishing maintenance service to $serviceOutput"
dotnet publish (Join-Path $root "src\OpenUninstaller.Service\OpenUninstaller.Service.csproj") @common -o $serviceOutput
if ($LASTEXITCODE -ne 0) { throw "Service publish failed with exit code $LASTEXITCODE." }

foreach ($file in @("README.md", "LICENSE", "CHANGELOG.md", "SECURITY.md")) {
    $source = Join-Path $root $file
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination $output -Force
    }
}

$docs = Join-Path $root "docs"
if (Test-Path $docs) {
    Copy-Item -LiteralPath $docs -Destination $output -Recurse -Force
}

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.Version

$manifest = [ordered]@{
    name = "OpenUninstaller"
    version = $version
    runtime = $Runtime
    configuration = $Configuration
    publishedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    executable = "OpenUninstaller.exe"
    cli = "cli\oucli.exe"
    maintenanceService = "service\OpenUninstaller.Service.exe"
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output "publish-manifest.json") -Encoding UTF8

Write-Host "Published to $output"
Get-ChildItem -LiteralPath $output | Select-Object Name, Length | Format-Table -AutoSize
