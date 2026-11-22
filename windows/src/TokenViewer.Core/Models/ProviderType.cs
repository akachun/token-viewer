namespace TokenViewer.Core.Models;

/// <summary>Supported AI subscription providers.</summary>
public enum ProviderType
{
    Codex,
    Claude,
    Gemini,
}

/// <summary>An sRGB color without alpha, independent of any UI framework.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromUnit(double r, double g, double b) =>
        new((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

public static class ProviderTypeExtensions
{
    public static readonly IReadOnlyList<ProviderType> All =
        [ProviderType.Codex, ProviderType.Claude, ProviderType.Gemini];

    /// <summary>Lowercase identifier matching the macOS app's raw values (used for storage keys).</summary>
    public static string Key(this ProviderType p) => p switch
    {
        ProviderType.Codex => "codex",
        ProviderType.Claude => "claude",
        ProviderType.Gemini => "gemini",
        _ => p.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this ProviderType p) => p switch
    {
        ProviderType.Codex => "Codex",
        ProviderType.Claude => "Claude",
        ProviderType.Gemini => "Gemini",
        _ => p.ToString(),
    };

    public static string CompanyName(this ProviderType p) => p switch
    {
        ProviderType.Codex => "OpenAI",
        ProviderType.Claude => "Anthropic",
        ProviderType.Gemini => "Google",
        _ => "",
    };

    public static string ShortLabel(this ProviderType p) => p switch
    {
        ProviderType.Codex => "CX",
        ProviderType.Claude => "CL",
        ProviderType.Gemini => "GM",
        _ => "",
    };

    public static string SingleLetter(this ProviderType p) => p switch
    {
        ProviderType.Codex => "O",
        ProviderType.Claude => "C",
        ProviderType.Gemini => "G",
        _ => "?",
    };

    public static RgbColor AccentColor(this ProviderType p) => p switch
    {
        ProviderType.Codex => RgbColor.FromUnit(0.06, 0.64, 0.50),  // Emerald / OpenAI green
        ProviderType.Claude => RgbColor.FromUnit(0.85, 0.47, 0.34), // Anthropic terracotta
        ProviderType.Gemini => RgbColor.FromUnit(0.26, 0.52, 0.96), // Google blue
        _ => new RgbColor(128, 128, 128),
    };

    public static Uri LoginUrl(this ProviderType p) => p switch
    {
        ProviderType.Codex => new Uri("https://chatgpt.com"),
        ProviderType.Claude => new Uri("https://claude.ai/login"),
        _ => new Uri("https://gemini.google.com"),
    };

    public static Uri UsageDashboardUrl(this ProviderType p) => p switch
    {
        ProviderType.Codex => new Uri("https://chatgpt.com/codex/settings/usage"),
        ProviderType.Claude => new Uri("https://claude.ai/settings/usage"),
        _ => new Uri("https://gemini.google.com"),
    };
}
