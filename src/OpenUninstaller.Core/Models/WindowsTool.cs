namespace OpenUninstaller.Core.Models;

public sealed record WindowsTool
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? Arguments { get; init; }
    public required string Description { get; init; }
    public string Category { get; init; } = "System";
    public string Glyph { get; init; } = "\uE71D";
    public bool RequiresElevation { get; init; }
    public bool IsCustom { get; init; }
}
