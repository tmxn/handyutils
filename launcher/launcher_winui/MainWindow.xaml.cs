using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.UI;
using Launcher;
using WinRT.Interop;

namespace LauncherWinUI;

public sealed partial class MainWindow : Window
{
    private readonly List<CategoryEntry> _categories = new();
    private readonly List<Button> _categoryButtons = new();
    private readonly List<Button> _itemButtons = new();
    private readonly Grid _itemPanel = new();
    private readonly TextBlock _emptyText = new();
    private readonly TextBlock _previewText = new();
    private readonly Palette _palette;
    private int _selectedIndex = -1;
    private bool _armed;
    private bool _heightPrimed; // once set, the window may only grow in height

    // Layout constants:
    // 700px window = margins(12) + track(148+21) + 3 columns of 165px cards.
    // TrackWidth matches the WinForms original (640 * 0.25 - 12 padding = 148).
    private const int WindowWidth = 700;
    private const int TrackWidth = 148;   // category button width, same as original
    private const int TrackGap = 21;      // spacing between headers and item grid
    private const int TrackColWidth = TrackWidth + TrackGap;
    private const int CardWidth = 165;    // slightly wider than the original 150 (no text wrap on cards)
    private const int CardHeight = 44;
    private const int CardGap = 6;
    private const int CategoryHeight = 58;
    private const int PreviewHeight = 26;

    public MainWindow()
    {
        InitializeComponent();

        _palette = SystemTheme.IsDarkMode() ? Palette.Dark : Palette.Light;

        // The window is made borderless via Native.MakeBorderless() in OnLoaded
        // (strips the caption frame), so the XAML content fills the whole
        // client area without a title-bar seam.

        BuildUi();

        // ElementTheme goes on the root element, not the Window.
        Root.RequestedTheme = SystemTheme.IsDarkMode() ? ElementTheme.Dark : ElementTheme.Light;

        // Per the toolchain directives, Window has no input/Loaded events;
        // they are wired on the root element instead.
        Root.Loaded += OnLoaded;
        Activated += OnActivated;
        Root.PointerWheelChanged += OnPointerWheel;
        Root.KeyDown += OnKeyDown;

        // Build everything and size/position the window BEFORE Activate() so the
        // first frame is already correct (no full-size flash at top-left).
        LoadConfig();
        BuildCategoryButtons();
        if (_categories.Count > 0)
            SelectCategory(0);
        SizeWindowToContent();

        // Park the window off-screen until it is fully composed. AppWindow.Move
        // before Activate() is unreliable (the OS repositions the window on
        // show, which is why the popup used to flash up at the top-left); the
        // real position is applied in OnLoaded, where Move sticks.
        AppWindow.Move(new PointInt32(-20000, 0));
    }

    // -------------------------------------------------------------------- UI

    private void BuildUi()
    {
        // Root: [body *][preview Auto]
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Body: [category track][item grid]
        var body = new Grid
        {
            Margin = new Thickness(6, 6, 6, 0),
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrackColWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 0);
        Root.Children.Add(body);

        // Left track (headers)
        var track = new StackPanel();
        Grid.SetColumn(track, 0);
        body.Children.Add(track);

        // Right: items + empty placeholder
        var itemHost = new Grid();
        Grid.SetColumn(itemHost, 1);
        body.Children.Add(itemHost);

        _itemPanel.Margin = new Thickness(0, 0, CardGap, 0);
        itemHost.Children.Add(_itemPanel);

        _emptyText.Text = "No items in this category";
        _emptyText.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyText.VerticalAlignment = VerticalAlignment.Center;
        _emptyText.Foreground = _palette.Text;
        _emptyText.FontSize = 13;
        _emptyText.Visibility = Visibility.Collapsed;
        itemHost.Children.Add(_emptyText);

        // Bottom: command preview line (Consolas, like the original). The
        // original drew the preview text on the transparent surface with no
        // separator line — a 1px top border here showed up as a hard line on
        // top of the preview area, so it is intentionally absent.
        var previewBorder = new Border
        {
            Height = PreviewHeight,
            Margin = new Thickness(6, 6, 6, 6),
        };
        _previewText.FontFamily = new FontFamily("Consolas");
        _previewText.FontSize = 11;
        _previewText.Foreground = _palette.PreviewForeground;
        _previewText.Margin = new Thickness(12, 0, 12, 0);
        _previewText.VerticalAlignment = VerticalAlignment.Center;
        _previewText.TextTrimming = TextTrimming.CharacterEllipsis;
        previewBorder.Child = _previewText;
        Grid.SetRow(previewBorder, 1);
        Root.Children.Add(previewBorder);
    }

    private void BuildCategoryButtons()
    {
        _categoryButtons.Clear();
        var track = (StackPanel)((Grid)Root.Children[0]).Children[0];
        track.Children.Clear();

        for (int i = 0; i < _categories.Count; i++)
        {
            int index = i;
            // Content is a wrapping TextBlock so long category names soft-wrap
            // to a second line instead of clipping.
            var label = new TextBlock
            {
                Text = _categories[i].Data.Name,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var button = new Button
            {
                Content = label,
                Width = TrackWidth,
                Height = CategoryHeight,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 14,
                Foreground = _palette.Text,
                Background = _palette.CategoryBackground,
                CornerRadius = new CornerRadius(8),
            };
            // Hover switches the category, like the original GlassButton.
            button.PointerEntered += (_, _) =>
            {
                SelectCategory(index);
                button.Background = index == _selectedIndex
                    ? _palette.CategorySelected
                    : _palette.CategoryHover;
            };
            button.PointerExited += (_, _) =>
                button.Background = index == _selectedIndex
                    ? _palette.CategorySelected
                    : _palette.CategoryBackground;
            track.Children.Add(button);
            _categoryButtons.Add(button);
        }
    }

    private void BuildItemButtons()
    {
        _itemButtons.Clear();
        _itemPanel.Children.Clear();
        _itemPanel.RowDefinitions.Clear();
        _itemPanel.ColumnDefinitions.Clear();

        var items = _selectedIndex >= 0
            ? _categories[_selectedIndex].Data.Items ?? new List<LauncherItem>()
            : new List<LauncherItem>();

        int rightAreaWidth = WindowWidth - TrackColWidth - 18;
        int cols = Math.Max(1, (rightAreaWidth + CardGap) / (CardWidth + CardGap));
        int rows = (items.Count + cols - 1) / cols;
        for (int r = 0; r < rows; r++)
            _itemPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cols; c++)
            _itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int row = i / cols;
            int col = i % cols;

            var button = new Button
            {
                Content = item.Name,
                Width = CardWidth,
                Height = CardHeight,
                Margin = new Thickness(0, 0, CardGap, CardGap),
                FontSize = 13,
                Foreground = _palette.Text,
                Background = _palette.CardBackground,
                CornerRadius = new CornerRadius(8),
                Tag = item,
            };
            button.PointerEntered += (_, _) =>
            {
                button.Background = _palette.CardHover;
                _previewText.Text = FormatCommand(item.Path, item.Args);
            };
            button.PointerExited += (_, _) =>
            {
                button.Background = _palette.CardBackground;
                _previewText.Text = "";
            };
            button.Click += (_, _) =>
            {
                Execute(item.Path, item.Args);
                Application.Current.Exit();
            };

            Grid.SetRow(button, row);
            Grid.SetColumn(button, col);
            _itemPanel.Children.Add(button);
            _itemButtons.Add(button);
        }

        _emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The WinForms original auto-sized the window to its content; do the same
    /// with AppWindow.Resize. Called after items are built and before show.
    /// After the first sizing the height only ever GROWS: hovering to a
    /// category with fewer items must not shrink the window from the bottom
    /// under the cursor (jarring); extra space just shows as an empty area.
    /// </summary>
    private void SizeWindowToContent()
    {
        int rightAreaWidth = WindowWidth - TrackColWidth - 18;
        int cols = Math.Max(1, (rightAreaWidth + CardGap) / (CardWidth + CardGap));
        int count = _itemButtons.Count;
        int rows = (count + cols - 1) / cols;
        int rightHeight = rows * (CardHeight + CardGap) + 6;

        int leftHeight = _categoryButtons.Count * (CategoryHeight + 6) + 6;
        int bodyHeight = Math.Max(leftHeight, rightHeight);

        int height = bodyHeight + PreviewHeight + 24;

        if (_heightPrimed && height <= AppWindow.Size.Height)
            return; // never shrink after the initial fit

        _heightPrimed = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, height));
    }

    // ------------------------------------------------------------ categories
    private void SelectCategory(int index)
    {
        if (index < 0 || index >= _categories.Count || index == _selectedIndex)
            return;

        _selectedIndex = index;
        for (int i = 0; i < _categoryButtons.Count; i++)
        {
            _categoryButtons[i].Background = i == index
                ? _palette.CategorySelected
                : _palette.CategoryBackground;
        }

        BuildItemButtons();
        SizeWindowToContent();
    }

    // ---------------------------------------------------------------- config

    private void LoadConfig()
    {
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".launcher");
        string configPath = Path.Combine(configDir, "config.json");

        Directory.CreateDirectory(configDir);
        if (!File.Exists(configPath))
            CreateDefaultConfig(configPath);

        string json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize(json, Launcher.AppConfigContext.Default.AppConfig)
                     ?? new Launcher.AppConfig();

        foreach (var category in config.Categories)
            _categories.Add(new CategoryEntry(category));
    }

    private static void CreateDefaultConfig(string path)
    {
        var config = new Launcher.AppConfig
        {
            Categories = new List<CategoryData>
            {
                new()
                {
                    Name = "Data Utilities",
                    Items = new List<LauncherItem>
                    {
                        new() { Name = "Terminal", Path = "wt.exe", Args = "" },
                        new() { Name = "VS Code", Path = "code", Args = "." },
                        new() { Name = "File Explorer", Path = "explorer", Args = "." },
                    },
                },
                new()
                {
                    Name = "System Controls",
                    Items = new List<LauncherItem>
                    {
                        new() { Name = "Settings", Path = "ms-settings:", Args = "" },
                        new() { Name = "Task Manager", Path = "taskmgr.exe", Args = "" },
                        new() { Name = "PowerShell", Path = "pwsh.exe", Args = "-NoExit" },
                    },
                },
            },
        };

        File.WriteAllText(path, JsonSerializer.Serialize(config, Launcher.AppConfigContext.Default.AppConfig));
    }

    // ----------------------------------------------------------------- items

    private static void Execute(string path, string args)
    {
        // Same as the original: shell-execute so the launcher never blocks on
        // the spawned process.
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            Arguments = args,
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    private static string FormatCommand(string path, string args)
    {
        string cmd = path + (string.IsNullOrEmpty(args) ? "" : " " + args);
        cmd = cmd.Replace("-NoExit -File ", "").Replace("-File ", "").Trim();

        string userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\";
        cmd = cmd.Replace(userDir, "~\\");

        return cmd;
    }

    // ----------------------------------------------------------- window flow

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
        {
            // Borderless + native Win11 rounded corners; the DWM call is the
            // same corner preference the old GlassInterop set (Win10 fallback).
            Native.MakeBorderless(hwnd);
            Native.RoundCorners(hwnd);
        }

        // Final sizing + the real popup position, now that the HWND exists and
        // SetWindowPos is honoured (the off-screen park in the constructor only
        // kept the first visible frame out of the way).
        App.Log("OnLoaded: sizing + positioning");
        SizeWindowToContent();
        ShowAtCursor();

        // Native Mica on Windows 11+ (high-level SystemBackdrop API handles
        // backdrop targets, theme switching and input focus automatically);
        // solid fallback surface on Win10.
        if (SystemTheme.IsWindows11())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        else
        {
            Root.Background = _palette.FallbackBackground;
        }

        // Arm the auto-exit only once the window is fully up and the message
        // queue has drained, mirroring the WinForms OnShown/BeginInvoke guard.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => _armed = true);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_categories.Count == 0)
            return;

        int direction = e.GetCurrentPoint(Root).Properties.MouseWheelDelta > 0 ? -1 : 1;
        int newIndex = _selectedIndex + direction;

        if (newIndex >= 0 && newIndex < _categories.Count)
        {
            SelectCategory(newIndex);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Application.Current.Exit();
        }
    }

    // Cold-start contract, like the WinForms version: the window is a popup —
    // as soon as focus leaves it, the process exits.
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated && _armed)
        {
            AppWindow.Hide();
            Application.Current.Exit();
        }
    }

    private void ShowAtCursor()
    {
        if (!Native.GetCursorPos(out Native.Point cursor))
            return;

        int width = WindowWidth;
        int height = Math.Max(1, AppWindow.Size.Height);

        Native.Rect workArea = Native.GetWorkArea(cursor);

        int x = cursor.X - width / 2;
        int y = cursor.Y - 40;

        if (x < workArea.Left) x = workArea.Left;
        if (y < workArea.Top) y = workArea.Top;
        if (x + width > workArea.Right) x = Math.Max(workArea.Left, workArea.Right - width);
        if (y + height > workArea.Bottom) y = Math.Max(workArea.Top, workArea.Bottom - height);

        MoveHwnd(x, y, $"cursor=({cursor.X},{cursor.Y}) work=({workArea.Left},{workArea.Top},{workArea.Right},{workArea.Bottom})", "show");

        // The OS can still re-place the window as part of the show sequence
        // after Loaded returns; re-apply the position once the message queue
        // has drained so the final resting place is guaranteed.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            MoveHwnd(x, y, $"cursor=({cursor.X},{cursor.Y})", "post-show"));
    }

    private void MoveHwnd(int x, int y, string context, string tag)
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;

        Native.MoveWindow(hwnd, x, y);

        // Read the position back so the log proves where the window actually
        // ended up (this is what was unverifiable with AppWindow.Move).
        if (Native.GetWindowRect(hwnd, out Native.Rect actual))
            App.Log($"ShowAtCursor[{tag}] {context} want=({x},{y}) actual=({actual.Left},{actual.Top}) size={actual.Right - actual.Left}x{actual.Bottom - actual.Top}");
        else
            App.Log($"ShowAtCursor[{tag}] {context} want=({x},{y}) GetWindowRect failed");
    }
}

/// <summary>ViewModel wrapper so the shared JSON model stays untouched.</summary>
public sealed class CategoryEntry
{
    public CategoryEntry(CategoryData data) => Data = data;
    public CategoryData Data { get; }
}

/// <summary>
/// The old ThemeHelper palette, moved into brushes. Mica-mode colours are
/// semi-transparent "cards" over the native backdrop.
/// </summary>
public sealed record Palette
{
    public SolidColorBrush CategoryBackground { get; init; } = null!;
    public SolidColorBrush CategoryHover { get; init; } = null!;
    public SolidColorBrush CategorySelected { get; init; } = null!;
    public SolidColorBrush CardBackground { get; init; } = null!;
    public SolidColorBrush CardHover { get; init; } = null!;
    public SolidColorBrush Text { get; init; } = null!;
    public SolidColorBrush PreviewForeground { get; init; } = null!;
    public SolidColorBrush PreviewBorder { get; init; } = null!;
    public SolidColorBrush FallbackBackground { get; init; } = null!;

    private static SolidColorBrush B(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));

    public static Palette Dark { get; } = new()
    {
        CategoryBackground = B(0x30, 0xFF, 0xFF, 0xFF),
        CategoryHover = B(0x56, 0xFF, 0xFF, 0xFF),
        CategorySelected = B(0x56, 0xFF, 0xFF, 0xFF),
        CardBackground = B(0x30, 0xFF, 0xFF, 0xFF),
        CardHover = B(0x56, 0xFF, 0xFF, 0xFF),
        Text = B(0xFF, 0xF3, 0xF3, 0xF3),
        PreviewForeground = B(0xFF, 0xB4, 0xB4, 0xB4),
        PreviewBorder = B(0x29, 0xFF, 0xFF, 0xFF),
        FallbackBackground = B(0xFF, 0x20, 0x20, 0x20),
    };

    public static Palette Light { get; } = new()
    {
        CategoryBackground = B(0x6E, 0xFF, 0xFF, 0xFF),
        CategoryHover = B(0x8C, 0xA6, 0xE6, 0xFA),
        CategorySelected = B(0x8C, 0xA6, 0xE6, 0xFA),
        CardBackground = B(0x6E, 0xFF, 0xFF, 0xFF),
        CardHover = B(0x8C, 0xA6, 0xE6, 0xFA),
        Text = B(0xFF, 0x1C, 0x1C, 0x1C),
        PreviewForeground = B(0xFF, 0x50, 0x50, 0x50),
        PreviewBorder = B(0x28, 0x00, 0x00, 0x00),
        FallbackBackground = B(0xFF, 0xF3, 0xF3, 0xF3),
    };
}
