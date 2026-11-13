// How the caption beside the pointer looks (Settings → Clicky → Talking): the colour of its words and how much they
// glow. "White on red" is the bubble OpenClicky has always had; every other colour writes its words on a dark,
// see-through card, so they read on a white page and on a dark screen alike. The glow is a soft halo in the words'
// own colour. Saved in Preferences next to the pointer's look (PointerLook).

namespace OpenClicky.Core.Pointing;

public enum CaptionGlow
{
    Off,
    Soft,
    Strong,
}

public static class CaptionLook
{
    public const string ColorPreferenceKey = "caption.color";
    public const string GlowPreferenceKey = "caption.glow";
    public const string DefaultColor = "classic";

    /// <summary>The colours on offer, by name: the classic bubble first (no hex: white words on the red bubble), then
    /// bright colours for words on the dark card.</summary>
    public static IReadOnlyList<(string Name, string? Hex, string Title)> Colors { get; } =
    [
        ("classic", null, "White on red"), ("orange", "#FF9A2E", "Orange"), ("amber", "#FFC43D", "Amber"),
        ("yellow", "#FFE94A", "Yellow"), ("white", "#FFFFFF", "White"), ("cyan", "#4FE3FF", "Cyan"),
        ("green", "#6BFF8E", "Green"), ("pink", "#FF6FB5", "Pink"), ("violet", "#B79BFF", "Violet"),
    ];

    public static IReadOnlyList<CaptionGlow> Glows { get; } = Enum.GetValues<CaptionGlow>();

    /// <summary>The stored colour's name; anything unknown is the classic bubble.</summary>
    public static string ColorFrom(string? stored) =>
        Colors.FirstOrDefault(color => string.Equals(color.Name, stored?.Trim(), StringComparison.OrdinalIgnoreCase)).Name ?? DefaultColor;

    /// <summary>The words' colour, or null for the classic bubble (white words on the pointer red).</summary>
    public static string? HexFor(string? stored) => Colors.First(color => color.Name == ColorFrom(stored)).Hex;

    public static string RawValue(this CaptionGlow glow) => glow.ToString().ToLowerInvariant();

    /// <summary>The stored glow; anything unknown is off.</summary>
    public static CaptionGlow GlowFrom(string? stored) =>
        Glows.FirstOrDefault(glow => string.Equals(glow.RawValue(), stored?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Title(this CaptionGlow glow) => glow switch
    {
        CaptionGlow.Soft => "Soft",
        CaptionGlow.Strong => "Strong",
        _ => "Off",
    };

    /// <summary>The halo around the words: its blur radius (DIPs) and opacity; none when off. A strong glow also lights
    /// the card's edge in the words' colour (the overlay draws that).</summary>
    public static (double BlurRadius, double Opacity)? Halo(this CaptionGlow glow) => glow switch
    {
        CaptionGlow.Soft => (4, 0.7),
        CaptionGlow.Strong => (10, 1.0),
        _ => null,
    };
}
