// How the pointer looks (2026-11-01): its colour and its shape, chosen in Settings → Clicky → Pointer. The pointer
// keeps its glow and its flights whatever it looks like; only the drawing of the mark changes.

namespace OpenClicky.Core.Pointing;

public enum PointerStyle
{
    /// <summary>The rounded triangle OpenClicky has always had.</summary>
    Triangle,
    /// <summary>A classic mouse-pointer arrow.</summary>
    Arrow,
    /// <summary>A filled dot, like a laser pointer.</summary>
    Dot,
    /// <summary>A hollow ring around what it points at.</summary>
    Ring,
}

public static class PointerLook
{
    public const string ColorPreferenceKey = "pointer.color";
    public const string StylePreferenceKey = "pointer.style";
    public const string DefaultColor = "red";

    /// <summary>The colours on offer, by name, with the red OpenClicky has always used first.</summary>
    public static IReadOnlyList<(string Name, string Hex, string Title)> Colors { get; } =
    [
        ("red", "#F0452B", "Red"), ("blue", "#2F6BFF", "Blue"), ("green", "#25A55F", "Green"), ("purple", "#7C5CD6", "Purple"),
        ("orange", "#F28C28", "Orange"), ("pink", "#E85D9C", "Pink"), ("teal", "#14A3A3", "Teal"), ("yellow", "#E8B400", "Yellow"),
        ("black", "#1E2230", "Black"), ("white", "#F4F5F7", "White"),
    ];

    public static IReadOnlyList<PointerStyle> Styles { get; } = Enum.GetValues<PointerStyle>();

    /// <summary>The colour's hex for a stored name (or a stored "#RRGGBB"); the default red for anything else.</summary>
    public static string HexFor(string? stored)
    {
        if (stored is { Length: 7 } hex && hex[0] == '#' && hex[1..].All(Uri.IsHexDigit)) return hex.ToUpperInvariant();
        return Colors.FirstOrDefault(color => string.Equals(color.Name, stored?.Trim(), StringComparison.OrdinalIgnoreCase)).Hex ?? Colors[0].Hex;
    }

    public static string RawValue(this PointerStyle style) => style.ToString().ToLowerInvariant();

    public static PointerStyle StyleFrom(string? stored) =>
        Styles.FirstOrDefault(style => string.Equals(style.RawValue(), stored?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Title(this PointerStyle style) => style switch
    {
        PointerStyle.Arrow => "Arrow",
        PointerStyle.Dot => "Dot",
        PointerStyle.Ring => "Ring",
        _ => "Triangle",
    };
}
