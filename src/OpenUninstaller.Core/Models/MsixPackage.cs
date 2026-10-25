namespace OpenUninstaller.Core.Models;

/// <summary>
/// One installed MSIX/Store package. Inventory only: Windows owns package activation, updates and
/// removal, so OpenUninstaller never mutates a package or its appx manifest.
/// </summary>
public sealed record MsixPackage
{
    public required string PackageFullName { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public string? Architecture { get; init; }
    public string? InstallLocation { get; init; }
    public bool IsFramework { get; init; }
    public bool IsResourcePackage { get; init; }

    public string KindDisplay => IsFramework ? "Framework" : IsResourcePackage ? "Resource" : "App";
}
