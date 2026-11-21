using Frost.Core;

namespace Frost.Core.Tests;

public sealed class ColorPaletteTests
{
    private static uint[] Fill(params (uint Color, int N)[] parts) => parts.SelectMany(p => Enumerable.Repeat(p.Color, p.N)).ToArray();

    [Fact]
    public void Most_common_first_and_limited()
    {
        var px = Fill((0xFFFF0000, 50), (0xFF0000FF, 30), (0xFF00FF00, 15), (0xFFFFFF00, 4), (0xFF00FFFF, 3), (0xFFFF00FF, 2));
        var p = ColorPalette.Extract(px, 5);
        Assert.Equal(5, p.Count);
        Assert.Equal(new uint[] { 0xFFFF0000, 0xFF0000FF, 0xFF00FF00 }, p.Take(3));
        Assert.Equal(2, ColorPalette.Extract(Fill((0xFFFF0000, 5), (0xFF0000FF, 1)), 5).Count);
    }

    [Fact]
    public void Ignores_transparent_and_merges_near_duplicates()
    {
        var px = Fill((0x00FF0000, 100), (0xFF102030, 20), (0xFF122232, 20), (0xFF112131, 20), (0xFFE0E0E0, 10));
        var p = ColorPalette.Extract(px, 5);
        Assert.Equal(2, p.Count);
        Assert.Equal(0xFFu, p[0] >> 24);
        Assert.True((p[0] >> 16 & 255) is >= 0x0F and <= 0x14); // the blue-grey cluster, not the transparent red
        Assert.Empty(ColorPalette.Extract(Fill((0x00000000, 10)), 5));
        Assert.Empty(ColorPalette.Extract(ReadOnlySpan<uint>.Empty, 5));
    }

    [Fact]
    public void Hex() => Assert.Equal("#A1B2C3", ColorPalette.Hex(0xFFA1B2C3));
}
