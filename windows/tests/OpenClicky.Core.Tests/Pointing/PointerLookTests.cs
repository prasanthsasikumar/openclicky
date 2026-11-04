// The pointer's look: stored colour names and hexes, and the shapes.

using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Tests.Pointing;

public class PointerLookTests
{
    [Theory]
    [InlineData(null, "#F0452B")]
    [InlineData("red", "#F0452B")]
    [InlineData("Blue", "#2F6BFF")]
    [InlineData("#12ab9f", "#12AB9F")]
    [InlineData("chartreuse", "#F0452B")]
    [InlineData("#12ab9", "#F0452B")]
    public void AStoredColourIsItsHexElseTheRedItAlwaysWas(string? stored, string hex) => Assert.Equal(hex, PointerLook.HexFor(stored));

    [Fact]
    public void ShapesAreStoredByName()
    {
        Assert.Equal(PointerStyle.Ring, PointerLook.StyleFrom("ring"));
        Assert.Equal(PointerStyle.Triangle, PointerLook.StyleFrom(null));
        Assert.Equal(PointerStyle.Triangle, PointerLook.StyleFrom("star"));
        Assert.Equal("arrow", PointerStyle.Arrow.RawValue());
        Assert.Equal(4, PointerLook.Styles.Count);
    }
}
