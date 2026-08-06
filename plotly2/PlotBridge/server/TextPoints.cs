using System.Globalization;
using System.Text.RegularExpressions;

namespace PlotBridge.Server;

/// <summary>
/// Turns loosely-formatted text into coordinate arrays. Tolerant on purpose: it
/// accepts clean TSV/CSV but also raw "Copy Value" output pasted straight out of
/// the Visual Studio debugger, e.g.
/// <code>
/// [0] {X=1.5 Y=2.25 Z=0}
/// [1] {X=1.75 Y=2.5 Z=0}
/// </code>
/// The leading <c>[n]</c> element index is stripped so it isn't mistaken for a
/// coordinate; everything else falls out of a plain number scan.
/// </summary>
public static partial class TextPoints
{
    [GeneratedRegex(@"^\s*\[\s*\d+\s*\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex IndexPrefix();

    [GeneratedRegex(@"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    public readonly record struct Result(double[] X, double[] Y, double[]? Z, int Skipped);

    public static Result Parse(string text)
    {
        var rows = new List<double[]>();
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            line = IndexPrefix().Replace(line, "");

            var nums = new List<double>(4);
            foreach (Match m in Number().Matches(line))
            {
                if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && !double.IsNaN(d) && !double.IsInfinity(d))
                    nums.Add(d);
                if (nums.Count == 3) break;   // never need more than x,y,z
            }

            if (nums.Count == 0) { skipped++; continue; }
            rows.Add(nums.ToArray());
        }

        if (rows.Count == 0) return new Result([], [], null, skipped);

        // Dimension is what *every* row can supply, capped at 3.
        var dim = 3;
        foreach (var r in rows) dim = Math.Min(dim, r.Length);

        if (dim <= 1)
        {
            // A single column of numbers: plot value against its index.
            var yv = new double[rows.Count];
            var xv = new double[rows.Count];
            for (var i = 0; i < rows.Count; i++) { xv[i] = i; yv[i] = rows[i][0]; }
            return new Result(xv, yv, null, skipped);
        }

        var x = new double[rows.Count];
        var y = new double[rows.Count];
        var z = dim >= 3 ? new double[rows.Count] : null;
        for (var i = 0; i < rows.Count; i++)
        {
            x[i] = rows[i][0];
            y[i] = rows[i][1];
            if (z is not null) z[i] = rows[i][2];
        }
        return new Result(x, y, z, skipped);
    }
}
