// The caption's look (Pointing/CaptionLook): its colours and glows by stored name, the classic bubble for anything unknown.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class CaptionLookTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("classic", null)]
    [InlineData("orange", "#FF9A2E")]
    [InlineData(" Orange ", "#FF9A2E")]
    [InlineData("cyan", "#4FE3FF")]
    [InlineData("chartreuse", null)]
    public void AStoredColourIsItsHexElseTheClassicBubble(string? stored, string? hex) => Assert.Equal(hex, CaptionLook.HexFor(stored));

    [Fact]
    public void TheClassicBubbleComesFirstAndEveryOtherColourIsAHex()
    {
        Assert.Equal(CaptionLook.DefaultColor, CaptionLook.Colors[0].Name);
        Assert.Null(CaptionLook.Colors[0].Hex);
        Assert.All(CaptionLook.Colors.Skip(1), color => Assert.Matches("^#[0-9A-F]{6}$", color.Hex));
        Assert.Equal(CaptionLook.Colors.Count, CaptionLook.Colors.Select(color => color.Name).Distinct().Count());
        Assert.Equal("classic", CaptionLook.ColorFrom("unknown"));
    }

    [Fact]
    public void GlowsAreStoredByNameAndOffHasNoHalo()
    {
        Assert.Equal(CaptionGlow.Strong, CaptionLook.GlowFrom("strong"));
        Assert.Equal(CaptionGlow.Off, CaptionLook.GlowFrom(null));
        Assert.Equal(CaptionGlow.Off, CaptionLook.GlowFrom("blinding"));
        Assert.Equal("soft", CaptionGlow.Soft.RawValue());
        Assert.Null(CaptionGlow.Off.Halo());
        Assert.True(CaptionGlow.Strong.Halo()!.Value.BlurRadius > CaptionGlow.Soft.Halo()!.Value.BlurRadius);
    }
}
