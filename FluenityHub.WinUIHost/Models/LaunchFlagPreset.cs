namespace FluenityHub_WinUIHost.Models;

public sealed record LaunchFlagPreset(
    string Name,
    string Flags,
    string Description,
    string IconGlyph)
{
    public static readonly IReadOnlyList<LaunchFlagPreset> BuiltInPresets =
    [
        new("Default", "", "Normal Editor launch without extra arguments", "\uE768"),
        new("Safe Mode", "-ignorecompilererrors", "Bypass script compilation errors on startup", "\uE73A"),
        new("Vulkan Mode", "-force-vulkan", "Force Vulkan graphics API rendering", "\uE774"),
        new("DirectX 12 Mode", "-force-d3d12", "Force DirectX 12 graphics API rendering", "\uE734"),
        new("Headless Server", "-batchmode -nographics", "Run in background without graphics window", "\uE896"),
        new("Profiler Mode", "-profile-editor", "Enable deep Editor performance profiling", "\uE9D2")
    ];
}
