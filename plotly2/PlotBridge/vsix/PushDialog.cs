using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// Where should these points go. Built in code rather than XAML — it is a
    /// four-field form, and a code-only window keeps the classic VSIX project free
    /// of XAML compilation and resource plumbing.
    /// </summary>
    internal sealed class PushDialog : Window
    {
        private readonly TextBox _board;
        private readonly TextBox _chart;
        private readonly TextBox _series;
        private readonly ComboBox _mode;
        private readonly CheckBox _replace;
        private readonly CheckBox _dontAsk;

        public string Board => _board.Text.Trim();
        public string Chart => _chart.Text.Trim();
        public string Series => _series.Text.Trim();
        public string Mode => (string)((ComboBoxItem)_mode.SelectedItem).Tag;
        public bool Replace => _replace.IsChecked == true;
        public bool DontAskAgain => _dontAsk.IsChecked == true;

        public PushDialog(IntPtr ownerHwnd, string suggestedSeries, string typeName, int pointCount, long elapsedMs)
        {
            Title = "Plot with PlotBridge";
            SizeToContent = SizeToContent.Height;
            Width = 460;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            if (ownerHwnd != IntPtr.Zero)
            {
                new WindowInteropHelper(this) { Owner = ownerHwnd };
            }

            var root = new StackPanel { Margin = new Thickness(14) };

            var summary = pointCount.ToString("N0") + " point" + (pointCount == 1 ? "" : "s") +
                          " read in " + elapsedMs + " ms";
            if (!string.IsNullOrEmpty(typeName)) summary += "\n" + typeName;
            root.Children.Add(new TextBlock
            {
                Text = summary,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
            });

            _series = AddField(root, "Series name", suggestedSeries);
            _chart = AddField(root, "Chart", Settings.Chart);
            _board = AddField(root, "Board", Settings.Board);

            root.Children.Add(new TextBlock { Text = "Mode", Margin = new Thickness(0, 6, 0, 2) });
            _mode = new ComboBox();
            foreach (var pair in new[] { ("auto", "auto — 3D if the points have a third number"), ("2d", "2D"), ("3d", "3D") })
            {
                var item = new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1 };
                _mode.Items.Add(item);
                if (Settings.Mode == pair.Item1) _mode.SelectedItem = item;
            }
            if (_mode.SelectedItem == null) _mode.SelectedIndex = 0;
            root.Children.Add(_mode);

            _replace = new CheckBox
            {
                Content = "Replace a series of the same name",
                IsChecked = Settings.Replace,
                Margin = new Thickness(0, 12, 0, 0),
            };
            root.Children.Add(_replace);

            _dontAsk = new CheckBox
            {
                Content = "Plot straight away from now on (don't show this again)",
                Margin = new Thickness(0, 6, 0, 0),
            };
            root.Children.Add(_dontAsk);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            var openPage = new Button { Content = "Open page", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            openPage.Click += (_, __) =>
            {
                try { Process.Start(PlotBridgeClient.PageUrl(Board)); } catch { }
            };

            var ok = new Button { Content = "Plot", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) => { DialogResult = true; };

            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };

            buttons.Children.Add(openPage);
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (_, __) => { _series.SelectAll(); _series.Focus(); };
        }

        private static TextBox AddField(Panel parent, string label, string value)
        {
            parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var box = new TextBox { Text = value ?? "" };
            parent.Children.Add(box);
            return box;
        }
    }
}
