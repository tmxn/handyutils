using System.Globalization;
using System.Text;

namespace PlotBridge.Server;

/// <summary>
/// Board state back out as text — the counterpart to the push pipeline. Anything
/// that got in can be piped to a file, a shell, or another process without a page
/// being open, which is what makes the server usable from a script or an agent
/// loop rather than only from the browser.
/// </summary>
/// <remarks>
/// Two shapes, for two different jobs:
/// <list type="bullet">
/// <item><b>tsv/csv</b> — one row per point, long form, for awk/Import-Csv/pandas.
/// Deliberately *not* round-trippable: the leading chart/series/i columns would be
/// read as coordinates by <see cref="TextPoints"/>, which takes the first three
/// numbers on a line.</item>
/// <item><b>json/ndjson</b> — one object per series, shaped like
/// <see cref="PushRequest"/>, so each object can be POSTed straight back to
/// <c>/push</c>. ndjson is the one to reach for with jq.</item>
/// </list>
/// Numbers are written with the invariant culture, so a comma-decimal locale
/// can't turn <c>1.5</c> into a pair of values — the same trap
/// <c>Send-PlotBridge.ps1</c> avoids on the way in.
/// </remarks>
public static class Export
{
    public enum Format { Tsv, Csv, Json, Ndjson }

    public static bool TryParseFormat(string? text, out Format format)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "": case "tsv": case "txt": case "text": format = Format.Tsv; return true;
            case "csv": format = Format.Csv; return true;
            case "json": format = Format.Json; return true;
            case "ndjson": case "jsonl": format = Format.Ndjson; return true;
            default: format = Format.Tsv; return false;
        }
    }

    /// <summary>Content type for a response. ndjson gets <c>text/plain</c> rather
    /// than <c>application/x-ndjson</c> so a browser shows it instead of
    /// downloading it — this endpoint gets read by eye a lot.</summary>
    public static string ContentType(Format format) => format switch
    {
        Format.Json => "application/json; charset=utf-8",
        _ => "text/plain; charset=utf-8",
    };

    /// <summary>File extension for <c>?download=</c>, so a saved export opens in
    /// the right tool.</summary>
    public static string Extension(Format format) => format switch
    {
        Format.Csv => "csv",
        Format.Json => "json",
        Format.Ndjson => "ndjson",
        _ => "tsv",
    };

    /// <summary>Series matching <paramref name="chart"/> and <paramref name="series"/>,
    /// both optional and both matched case-insensitively. Hidden series are included:
    /// visibility is a page concern, and silently dropping data from an export is a
    /// worse surprise than an extra series.</summary>
    private static List<(Chart Chart, Series Series)> Select(Board board, string? chart, string? series)
    {
        var picked = new List<(Chart, Series)>();
        foreach (var c in board.Charts)
        {
            if (!string.IsNullOrWhiteSpace(chart) &&
                !c.Name.Equals(chart.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var s in c.Series)
            {
                if (!string.IsNullOrWhiteSpace(series) &&
                    !s.Name.Equals(series.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

                picked.Add((c, s));
            }
        }
        return picked;
    }

    /// <summary>How much the same filter would export. Both numbers are wanted:
    /// zero series means the filter matched nothing, whereas series with zero points
    /// means the chart exists but is empty - and a caller staring at an empty body
    /// cannot otherwise tell those apart.</summary>
    /// <remarks><see cref="Series.Count"/> is a *point* count, not a collection
    /// size, so the series total has to come from the list itself.</remarks>
    public static (int Series, int Points) Counts(Board board, string? chart, string? series)
    {
        var picked = Select(board, chart, series);
        return (picked.Count, picked.Sum(p => p.Series.Count));
    }

    public static string Render(Board board, string? chart, string? series, Format format)
    {
        var picked = Select(board, chart, series);
        return format switch
        {
            Format.Csv => Delimited(picked, ','),
            Format.Json => Structured(board, picked, pretty: true),
            Format.Ndjson => Structured(board, picked, pretty: false),
            _ => Delimited(picked, '\t'),
        };
    }

    // ---- long form ---------------------------------------------------------

    private static string Delimited(List<(Chart Chart, Series Series)> picked, char sep)
    {
        var sb = new StringBuilder();
        sb.Append("chart").Append(sep).Append("series").Append(sep).Append('i')
          .Append(sep).Append('x').Append(sep).Append('y').Append(sep).Append('z').Append('\n');

        foreach (var (chart, s) in picked)
        {
            var chartCell = Cell(chart.Name, sep);
            var seriesCell = Cell(s.Name, sep);

            for (var i = 0; i < s.Y.Length; i++)
            {
                sb.Append(chartCell).Append(sep).Append(seriesCell).Append(sep)
                  .Append(i.ToString(CultureInfo.InvariantCulture)).Append(sep)
                  .Append(Num(i < s.X.Length ? s.X[i] : i)).Append(sep)
                  .Append(Num(s.Y[i])).Append(sep);

                // Empty rather than 0 for a 2D series: a real zero and "no third
                // dimension" must not read the same downstream.
                if (s.Z is { } z && i < z.Length) sb.Append(Num(z[i]));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Escapes a name for one cell. CSV gets RFC 4180 quoting; TSV has no
    /// quoting convention worth honouring, so an embedded tab becomes a space —
    /// mangling a label beats shifting every column after it.</summary>
    private static string Cell(string value, char sep)
    {
        if (sep == '\t') return value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    // On .NET Core 3.0+ the default double format is the shortest string that
    // round-trips, so this needs no precision specifier — only the invariant culture.
    private static string Num(double v) =>
        double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture) : "";

    // ---- push-shaped -------------------------------------------------------

    private static string Structured(Board board, List<(Chart Chart, Series Series)> picked, bool pretty)
    {
        var sb = new StringBuilder();
        if (pretty) sb.Append("[\n");

        for (var n = 0; n < picked.Count; n++)
        {
            var (chart, s) = picked[n];
            var indent = pretty ? "  " : "";

            sb.Append(indent).Append('{');
            sb.Append("\"board\":").Append(Str(board.Name));
            sb.Append(",\"chart\":").Append(Str(chart.Name));
            sb.Append(",\"series\":").Append(Str(s.Name));
            sb.Append(",\"mode\":").Append(Str(chart.Mode));
            sb.Append(",\"visible\":").Append(s.Visible ? "true" : "false");
            sb.Append(",\"n\":").Append(s.Y.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"x\":").Append(Arr(s.X));
            sb.Append(",\"y\":").Append(Arr(s.Y));
            if (s.Z is { Length: > 0 }) sb.Append(",\"z\":").Append(Arr(s.Z));
            sb.Append(",\"style\":{\"mode\":").Append(Str(s.Style.Mode))
              .Append(",\"size\":").Append(Num0(s.Style.Size));
            if (s.Style.Slot is { } slot) sb.Append(",\"slot\":").Append(slot.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(s.Style.Color)) sb.Append(",\"color\":").Append(Str(s.Style.Color!));
            sb.Append('}');
            sb.Append('}');

            if (pretty) sb.Append(n < picked.Count - 1 ? ",\n" : "\n");
            else sb.Append('\n');
        }

        if (pretty) sb.Append("]\n");
        return sb.ToString();
    }

    private static string Arr(double[]? values)
    {
        if (values is null || values.Length == 0) return "[]";
        var sb = new StringBuilder(values.Length * 8);
        sb.Append('[');
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            // JSON has no NaN/Infinity literal, so a non-finite value becomes null
            // — which System.Text.Json on the way back in reads as a gap, not a zero.
            sb.Append(double.IsFinite(values[i]) ? values[i].ToString(CultureInfo.InvariantCulture) : "null");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string Num0(double v) =>
        double.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture) : "0";

    // The default encoder is HTML-safe, which turns "lines+markers" into
    // "lines+markers" - valid JSON, but this output gets read by eye and piped
    // through jq. Relaxed escaping is safe because the body is served as json/plain
    // and never interpolated into a page.
    private static readonly System.Text.Json.JsonSerializerOptions StringJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Str(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value, StringJson);
}
