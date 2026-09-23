using Microsoft.Win32;

namespace TaskbarThermals;

/// <summary>Colors for the panel and settings window. Chart series and status colors come from the validated dataviz palette.</summary>
internal sealed record Theme(
    bool Light,
    Color Surface,
    Color Raised,
    Color RaisedHover,
    Color Text,
    Color TextSecondary,
    Color TextMuted,
    Color Grid,
    Color Border,
    Color Cpu,
    Color Gpu)
{
    // Status colors are mode-invariant and always paired with an icon + label.
    public static readonly Color Good = Hex("#0ca30c");
    public static readonly Color Warning = Hex("#fab219");
    public static readonly Color Critical = Hex("#d03b3b");

    public static readonly Theme Dark = new(
        Light: false,
        Surface: Hex("#1f1f1f"),
        Raised: Hex("#2a2a2a"),
        RaisedHover: Hex("#353535"),
        Text: Hex("#ffffff"),
        TextSecondary: Hex("#c3c2b7"),
        TextMuted: Hex("#898781"),
        Grid: Hex("#2f2f2d"),
        Border: Hex("#3a3a3a"),
        Cpu: Hex("#3987e5"),
        Gpu: Hex("#d95926"));

    public static readonly Theme LightMode = new(
        Light: true,
        Surface: Hex("#f9f9f9"),
        Raised: Hex("#ffffff"),
        RaisedHover: Hex("#ececec"),
        Text: Hex("#0b0b0b"),
        TextSecondary: Hex("#52514e"),
        TextMuted: Hex("#898781"),
        Grid: Hex("#e1e0d9"),
        Border: Hex("#d6d6d6"),
        Cpu: Hex("#2a78d6"),
        Gpu: Hex("#eb6834"));

    /// <summary>Forces a theme (used by the --preview mode to render both).</summary>
    public static Theme? Override { get; set; }

    public static Theme Current => Override ?? (SystemUsesLightTheme() ? LightMode : Dark);

    public static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}

internal static class AppIcon
{
    public static Icon Get(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("TaskbarThermals.icon.ico")!;
        return new Icon(stream, size, size);
    }
}
