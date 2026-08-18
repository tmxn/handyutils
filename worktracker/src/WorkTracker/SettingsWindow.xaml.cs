using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkTracker.Services;

namespace WorkTracker;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<(string Name, string Email)> _unassigned;
    private readonly List<TextBox> _thresholdBoxes = new();
    private Developer? _editing;

    public SettingsWindow(AppConfig config, List<(string Name, string Email)> unassigned)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyWindowChrome();
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        _config = config;
        _unassigned = unassigned;

        ThemeCombo.ItemsSource = new[] { "auto", "light", "dark" };
        ThemeCombo.SelectedItem = new[] { "auto", "light", "dark" }.Contains(config.Theme)
            ? config.Theme : "auto";

        BackendCombo.ItemsSource = new[] { "pi", "llama.cpp" };
        BackendCombo.SelectedItem = config.Llm.Backend == "llama.cpp" ? "llama.cpp" : "pi";
        RepoPathBox.Text = config.RepoPath;
        LlmCommandBox.Text = config.Llm.Command;
        LlmArgsBox.Text = string.Join(' ', config.Llm.Args);
        LlmTimeoutBox.Text = config.Llm.TimeoutSeconds.ToString();
        ThinkingCombo.ItemsSource = new[]
        {
            "off", "minimal", "low", "medium", "high", "xhigh", "max",
        };
        ThinkingCombo.Text = config.Llm.ThinkingEffort; // free-form; defaults to the closest level
        LlamaEndpointBox.Text = string.IsNullOrWhiteSpace(config.Llm.LlamaEndpoint)
            ? "http://192.168.18.126:8080/" : config.Llm.LlamaEndpoint;
        LlamaModelBox.Text = config.Llm.LlamaModel;
        LlamaTimeoutBox.Text = config.Llm.TimeoutSeconds.ToString();
        LlamaThinkingCombo.ItemsSource = new[] { "off", "low", "medium", "high", "max" };
        LlamaThinkingCombo.Text = config.Llm.LlamaThinkingLevel;
        UpdateBackendFields();

        foreach (var t in config.Grid.LoadThresholds)
        {
            var box = new TextBox { Width = 50, Padding = new Thickness(3), Margin = new Thickness(0, 0, 6, 0) };
            box.Text = t.ToString();
            _thresholdBoxes.Add(box);
            ThresholdsPanel.Children.Add(box);
        }

        foreach (var dev in config.Developers)
            DeveloperList.Items.Add(new ComboBoxItem { Content = dev.DisplayName, Tag = dev });
        DeveloperList.SelectedIndex = config.Developers.Count > 0 ? 0 : -1;

        foreach (var (name, email) in unassigned)
            UnassignedList.Items.Add(new ListBoxItem { Content = $"{name} <{email}>" });
    }

    /// <summary>Win11: dark title bar + Mica backdrop (see Services/WindowChrome.cs).</summary>
    private void ApplyWindowChrome()
    {
        var mica = WindowChrome.Apply(this, Theme.Current == "dark");
        Background = mica ? Brushes.Transparent : Theme.Brush("WindowBackground");
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyWindowChrome);

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBackendFields();

    private void UpdateBackendFields()
    {
        var llama = string.Equals(BackendCombo.SelectedItem as string, "llama.cpp", StringComparison.OrdinalIgnoreCase);
        PiConfigPanel.Visibility = llama ? Visibility.Collapsed : Visibility.Visible;
        LlamaConfigPanel.Visibility = llama ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeveloperList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _editing = (DeveloperList.SelectedItem as ComboBoxItem)?.Tag as Developer;
        NoDevSelected.Visibility = _editing == null ? Visibility.Visible : Visibility.Collapsed;
        if (_editing == null) return;
        DisplayNameBox.Text = _editing.DisplayName;
        NamesBox.Text = string.Join("\n", _editing.AuthorNames);
        EmailsBox.Text = string.Join("\n", _editing.AuthorEmails);
    }

    private void AddDeveloper_Click(object sender, RoutedEventArgs e)
    {
        var dev = new Developer { DisplayName = "New developer", Id = Guid.NewGuid().ToString("N")[..6] };
        _config.Developers.Add(dev);
        var item = new ComboBoxItem { Content = dev.DisplayName, Tag = dev };
        DeveloperList.Items.Add(item);
        DeveloperList.SelectedItem = item;
    }

    private void RemoveDeveloper_Click(object sender, RoutedEventArgs e)
    {
        if (DeveloperList.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is Developer dev) _config.Developers.Remove(dev);
        DeveloperList.Items.Remove(item);
        _editing = null;
        NoDevSelected.Visibility = Visibility.Visible;
    }

    private void AddUnassigned_Click(object sender, RoutedEventArgs e)
    {
        if (UnassignedList.SelectedItem is not ListBoxItem item) return;
        var content = item.Content as string ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(content, @"^(.*?) <(.*)>$");
        var dev = new Developer
        {
            DisplayName = m.Groups[1].Value,
            Id = (ConfigStore.slug(m.Groups[1].Value) ?? "dev"),
            AuthorNames = new List<string> { m.Groups[1].Value },
            AuthorEmails = m.Groups[2].Value.Length > 0 ? new List<string> { m.Groups[2].Value } : new(),
        };
        _config.Developers.Add(dev);
        DeveloperList.Items.Add(new ComboBoxItem { Content = dev.DisplayName, Tag = dev });
        DeveloperList.SelectedItem = DeveloperList.Items[^1];
        UnassignedList.Items.Remove(item);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit edits for the currently edited developer first.
        CommitDeveloperEdits();

        _config.RepoPath = RepoPathBox.Text.Trim();
        if (_config.RepoPath.Length == 0)
        {
            MessageBox.Show(this, "Repo path is required.", "WorkTracker", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var backend = BackendCombo.SelectedItem as string ?? "pi";
        _config.Llm.Backend = backend == "llama.cpp" ? "llama.cpp" : "pi";
        _config.Llm.Command = LlmCommandBox.Text.Trim();
        _config.Llm.Args = LlmArgsBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var effort = ThinkingCombo.Text?.Trim() ?? "";
        _config.Llm.ThinkingEffort = string.IsNullOrEmpty(effort) ? "" : effort.TrimStart('-');
        _config.Llm.LlamaEndpoint = LlamaEndpointBox.Text.Trim();
        _config.Llm.LlamaModel = string.IsNullOrWhiteSpace(LlamaModelBox.Text) ? "any" : LlamaModelBox.Text.Trim();
        var llamaEffort = LlamaThinkingCombo.Text?.Trim() ?? "";
        _config.Llm.LlamaThinkingLevel = string.IsNullOrEmpty(llamaEffort) ? "low" : llamaEffort.TrimStart('-');
        var timeoutText = _config.Llm.Backend == "llama.cpp" ? LlamaTimeoutBox.Text : LlmTimeoutBox.Text;
        if (!int.TryParse(timeoutText.Trim(), out var timeout) || timeout <= 0)
        {
            MessageBox.Show(this, "Timeout must be a positive number of seconds.", "WorkTracker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_config.Llm.Backend == "llama.cpp" &&
            (!Uri.TryCreate(_config.Llm.LlamaEndpoint, UriKind.Absolute, out var endpoint) ||
             (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this, "llama.cpp endpoint must be an absolute http:// or https:// URL.", "WorkTracker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _config.Llm.TimeoutSeconds = timeout;
        _config.Theme = ThemeCombo.SelectedItem as string ?? "auto";

        foreach (var (box, i) in _thresholdBoxes.Select((b, i) => (b, i)))
        {
            if (!int.TryParse(box.Text.Trim(), out var v))
            {
                MessageBox.Show(this, $"Load threshold #{i + 1} must be an integer.", "WorkTracker",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _config.Grid.LoadThresholds[i] = v;
        }

        DialogResult = true;
    }

    private void CommitDeveloperEdits()
    {
        if (_editing == null) return;
        var name = DisplayNameBox.Text.Trim();
        if (name.Length > 0)
        {
            _editing.DisplayName = name;
            if (DeveloperList.SelectedItem is ComboBoxItem item)
                item.Content = name;
        }
        _editing.AuthorNames = NamesBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _editing.AuthorEmails = EmailsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrEmpty(_editing.Id))
            _editing.Id = ConfigStore.slug(name) ?? "dev";
    }
}
