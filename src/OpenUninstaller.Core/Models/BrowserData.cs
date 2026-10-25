namespace OpenUninstaller.Core.Models;

public enum BrowserDataKind
{
    History,
    Cookies
}

public sealed record BrowserDataFile(BrowserDataKind Kind, string Path, long Bytes)
{
    public string KindLabel => Kind == BrowserDataKind.History ? "History" : "Cookies";
}

public sealed class BrowserProfile
{
    public required string Browser { get; init; }
    public required string ProcessName { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfilePath { get; init; }
    public List<BrowserDataFile> Files { get; } = [];

    public long TotalBytes => Files.Sum(f => f.Bytes);
    public int HistoryCount => Files.Count(f => f.Kind == BrowserDataKind.History);
    public int CookieCount => Files.Count(f => f.Kind == BrowserDataKind.Cookies);
    public string SizeDisplay => InstalledProgram.FormatBytes(TotalBytes);
    public string Detail => $"{HistoryCount} history · {CookieCount} cookie file(s)";
    public string DisplayName => $"{Browser} · {ProfileName}";
}

public sealed class BrowserScanResult
{
    public List<BrowserProfile> Profiles { get; } = [];
    public List<string> Warnings { get; } = [];
    public long TotalBytes => Profiles.Sum(p => p.TotalBytes);
    public int TotalFiles => Profiles.Sum(p => p.Files.Count);
    public int HistoryCount => Profiles.Sum(p => p.HistoryCount);
    public int CookieCount => Profiles.Sum(p => p.CookieCount);
}

public sealed class BrowserCleanResult
{
    public bool DryRun { get; init; }
    public int DeletedFiles { get; set; }
    public long Bytes { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; } = [];
    public bool Succeeded => Messages.Count == 0 || DeletedFiles > 0;
    public string Summary => DryRun
        ? $"Would remove {DeletedFiles} browser file(s), {InstalledProgram.FormatBytes(Bytes)}."
        : $"Removed {DeletedFiles} browser file(s), {InstalledProgram.FormatBytes(Bytes)}.";
}
