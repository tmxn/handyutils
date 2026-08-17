namespace WorkTracker.Services;

/// <summary>
/// Normalizes LLM text output to plain ASCII. Some models emit decorative unicode
/// (curly quotes, em/en dashes, ellipses) that render inconsistently in some fonts.
/// Applied to all persisted LLM text so cached scores/reports stay clean.
/// </summary>
internal static class TextSanitizer
{
    public static string ToAscii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                // Curly single/double quotes and backtick.
                case '\u2018': case '\u2019': case '\u201A': case '\u201B': sb.Append('\''); break;
                case '\u201C': case '\u201D': case '\u201E': case '\u201F': sb.Append('"'); break;
                // Dashes.
                case '\u2013': case '\u2014': case '\u2015': case '\u2212': sb.Append('-'); break;
                // Ellipsis and spaces.
                case '\u2026': sb.Append("..."); break;
                case '\u00A0': case '\u2009': case '\u200A': case '\u2002': case '\u2003':
                case '\u2000': case '\u2001': case '\u2007': case '\u202F': sb.Append(' '); break;
                default:
                    if (ch >= 0x20) sb.Append(ch);
                    else sb.Append(' ');
                    break;
            }
        }
        return sb.ToString();
    }
}
