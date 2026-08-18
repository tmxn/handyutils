using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkTracker.Services;

namespace WorkTracker;

/// <summary>Row model for the day view commit list.</summary>
public sealed class CommitRow
{
    public CommitInfo Commit { get; init; } = null!;
    public ScoreEntry? Score { get; init; }

    public string TimeText => Commit.AuthorDate.ToString("HH:mm");
    public string Subject => Commit.Subject;
    public bool IsMerge => Commit.IsMerge;
    public string MergeTag => Commit.IsMerge ? "  [merge]" : "";
    public string TriageTag => Score?.Triage != null ? $"  [mechanical: {Score.Triage}]" : "";
    public string StatText =>
        $"{Commit.FilesChanged} files · added {Commit.Insertions} lines, removed {Commit.Deletions} lines" +
        (Commit.IsRevert ? " · revert" : "");

    public string ScoreText => Score?.Score.ToString() ?? "";
    public Visibility ScoreBadgeVisibility => Score == null ? Visibility.Collapsed : Visibility.Visible;
    public Brush ScoreBadgeBrush => Score == null ? Brushes.Transparent : UiPalette.ScoreBadge(Score.Score);

    public string CommentText => Score?.Comment ?? "";
    public Brush SubjectForeground => Commit.IsMerge
        ? UiPalette.MutedText
        : UiPalette.DarkText;

    public Visibility DiffVisibility =>
        Commit.IsMerge && Commit.Numstat.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string DiffText { get; set; } = "(loading…)";

    /// <summary>Merges are visually de-emphasized.</summary>
    public Brush MergeBackground => Commit.IsMerge
        ? UiPalette.MergeBackground
        : Theme.Brush("WindowBackground");
}

/// <summary>
/// WPF TextBlock is not user-selectable; for copyable text use this borderless
/// read-only TextBox that looks like a TextBlock. A plain wrapping TextBox does
/// not grow with its content, so this subclass keeps Height in sync with the
/// measured desired size (on text changes and width changes).
/// </summary>
public sealed class SelectableTextBox : TextBox
{
    public SelectableTextBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        TextWrapping = TextWrapping.Wrap;
        FocusVisualStyle = null;
        TextChanged += (_, _) => UpdateHeight();
        SizeChanged += (_, _) => UpdateHeight();
    }

    private void UpdateHeight()
    {
        if (double.IsNaN(ActualWidth) || ActualWidth <= 0)
        {
            Height = double.NaN; // not arranged yet; let layout size it initially
            return;
        }
        Measure(new Size(ActualWidth, double.PositiveInfinity));
        var h = DesiredSize.Height;
        if (h > 0 && Math.Abs(Height - h) > 0.5)
            Height = h;
    }
}

public static class Selectable
{
    public static SelectableTextBox Text(string content, double fontSize = 14,
        Brush? foreground = null, Thickness? margin = null, bool monospace = false)
    {
        var tb = new SelectableTextBox
        {
            Text = content,
            FontSize = fontSize,
            Foreground = foreground ?? UiPalette.DarkText,
            Margin = margin ?? new Thickness(0),
        };
        if (monospace) tb.FontFamily = new FontFamily("Consolas");
        return tb;
    }
}

internal static class UiPalette
{
    // Resolved from the active theme at use time (UI is rebuilt on theme change).
    public static Brush DarkText => Theme.Brush("TextPrimary");
    public static Brush MutedText => Theme.Brush("TextMuted");
    public static Brush LinkBlue => Theme.Brush("LinkBrush");
    public static Brush MergeBackground => Theme.Brush("MergeBackground");

    /// <summary>GitHub-style green ramp, reused for score badges (1–10 → 5 bands).</summary>
    public static Brush ScoreBadge(int score)
    {
        var c = score <= 2 ? Color.FromRgb(0x9B, 0xE9, 0xA8)
            : score <= 4 ? Color.FromRgb(0x40, 0xC4, 0x63)
            : score <= 6 ? Color.FromRgb(0x30, 0xA1, 0x4E)
            : score <= 8 ? Color.FromRgb(0x21, 0x6E, 0x39)
            : Color.FromRgb(0x1A, 0x55, 0x32);
        return new SolidColorBrush(c);
    }
}
