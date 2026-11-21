namespace Frost.Core;

internal static class PaletteImpl
{
    private const double Merge = 56; // RGB distance under which two colours count as the same swatch

    /// <summary>Histogram on 4 bits per channel, then greedy clustering from the most common bin; each cluster's colour is its weighted mean.</summary>
    public static IReadOnlyList<uint> Extract(ReadOnlySpan<uint> argb, int count)
    {
        if (count <= 0) return Array.Empty<uint>();
        var n = new int[4096];
        var sr = new long[4096]; var sg = new long[4096]; var sb = new long[4096];
        foreach (uint p in argb)
        {
            if ((p >> 24) < 128) continue;
            int r = (int)(p >> 16 & 255), g = (int)(p >> 8 & 255), b = (int)(p & 255);
            int k = r >> 4 << 8 | g >> 4 << 4 | b >> 4;
            n[k]++; sr[k] += r; sg[k] += g; sb[k] += b;
        }
        var cl = new List<(double R, double G, double B, long N)>();
        foreach (int k in Enumerable.Range(0, 4096).Where(k => n[k] > 0).OrderByDescending(k => n[k]))
        {
            double r = sr[k] / (double)n[k], g = sg[k] / (double)n[k], b = sb[k] / (double)n[k];
            int best = -1; double bd = Merge;
            for (int i = 0; i < cl.Count; i++)
            {
                double d = Math.Sqrt(Sq(r - cl[i].R) + Sq(g - cl[i].G) + Sq(b - cl[i].B));
                if (d < bd) { bd = d; best = i; }
            }
            if (best >= 0)
            {
                var c = cl[best]; long t = c.N + n[k];
                cl[best] = ((c.R * c.N + sr[k]) / t, (c.G * c.N + sg[k]) / t, (c.B * c.N + sb[k]) / t, t);
            }
            else cl.Add((r, g, b, n[k])); // more than `count` clusters are kept while merging; trimmed below
        }
        return cl.OrderByDescending(c => c.N).Take(count)
            .Select(c => 0xFF000000u | (uint)Math.Round(c.R) << 16 | (uint)Math.Round(c.G) << 8 | (uint)Math.Round(c.B)).ToList();
    }

    private static double Sq(double x) => x * x;
}
