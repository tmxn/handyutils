using System.Windows.Controls;

namespace WorkTracker;

public partial class DayView : UserControl
{
    public DayView()
    {
        InitializeComponent();
    }

    public void Show(DateTime day, IReadOnlyList<CommitRow> rows)
    {
        TitleText.Text = day.ToString("dddd, yyyy-MM-dd");
        var nonMerge = rows.Count(r => !r.IsMerge);
        SubtitleText.Text = $"{rows.Count} commits ({nonMerge} non-merge)";
        CommitItems.ItemsSource = rows;
    }
}
