using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Launcher;

public partial class LauncherForm : Form
{
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    private FlowLayoutPanel _leftPanel = new();
    private FlowLayoutPanel _rightPanel = new();
    private Label _commandPreview = new();
    private List<GlassButton> _categoryButtons = new();
    private List<CategoryData> _categories = new();
    private CategoryData? _activeCategory;
    private int _selectedCategoryIndex = -1;
    private LauncherPalette _palette;

    private static readonly Font CommandFont = new("Consolas", 9f, FontStyle.Regular);

    private static readonly Font CategoryFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Font ItemFont = new("Segoe UI", 9.5f, FontStyle.Regular);
    private static readonly Font EmptyFont = new("Segoe UI", 9f, FontStyle.Regular);

    public LauncherForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 640;
        DoubleBuffered = true;

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);

        // Left panel: vertical flow for categories (25%)
        _leftPanel.Dock = DockStyle.Left;
        _leftPanel.Width = (int)(Width * 0.25);
        _leftPanel.FlowDirection = FlowDirection.TopDown;
        _leftPanel.WrapContents = false;
        _leftPanel.AutoScroll = true;
        _leftPanel.Padding = new Padding(6, 6, 6, 6);
        _leftPanel.Margin = Padding.Empty;

        // Right panel: flow grid for items (75%)
        _rightPanel.Dock = DockStyle.Fill;
        _rightPanel.WrapContents = true;
        _rightPanel.AutoScroll = true;
        _rightPanel.AutoSize = false;
        _rightPanel.Padding = new Padding(6, 6, 6, 6);
        _rightPanel.Margin = Padding.Empty;

        // Command preview bar at bottom
        _commandPreview.Dock = DockStyle.Bottom;
        _commandPreview.Height = 26;
        _commandPreview.Font = CommandFont;
        _commandPreview.Padding = new Padding(8, 4, 8, 4);
        _commandPreview.Text = "";
        _commandPreview.AutoEllipsis = true;

        Controls.Add(_commandPreview);
        Controls.Add(_rightPanel);
        Controls.Add(_leftPanel);

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeWindowEffects();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        ApplyTheme();
        LoadConfig();
        AutoSizeWindow();

        // Cold start: show once at cursor and exit on deactivate.
        ShowAtCursor();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _leftPanel.Width = (int)(Width * 0.25);

        // Resize category buttons to fit
        foreach (GlassButton btn in _categoryButtons)
            btn.Width = _leftPanel.Width - _leftPanel.Padding.Left - _leftPanel.Padding.Right;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General ||
            e.Category == UserPreferenceCategory.Color)
        {
            ApplyTheme();
        }
    }

    private void ApplyTheme()
    {
        _palette = ThemeHelper.GetPalette();

        BackColor = _palette.FormBackground;
        _leftPanel.BackColor = _palette.LeftPanelBackground;
        _rightPanel.BackColor = _palette.RightPanelBackground;
        _commandPreview.BackColor = _palette.FormBackground;
        _commandPreview.ForeColor = ThemeHelper.IsDarkMode()
            ? Color.FromArgb(180, 180, 180)
            : Color.FromArgb(80, 80, 80);

        ApplyNativeWindowEffects();

        Invalidate(true);
        foreach (Control c in _leftPanel.Controls) c.Invalidate();
        foreach (Control c in _rightPanel.Controls) c.Invalidate();
    }

    private void ApplyNativeWindowEffects()
    {
        if (!IsHandleCreated)
            return;

        GlassInterop.EnableNativeWindowEffects(Handle, ThemeHelper.IsDarkMode());
    }

    private void LoadConfig()
    {
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".launcher");
        string configPath = Path.Combine(configDir, "config.json");

        Directory.CreateDirectory(configDir);

        if (!File.Exists(configPath))
            CreateDefaultConfig(configPath);

        string json = File.ReadAllText(configPath);
        _categories = JsonSerializer.Deserialize(json, AppConfigContext.Default.AppConfig)?.Categories ?? new List<CategoryData>();

        int btnWidth = _leftPanel.Width - _leftPanel.Padding.Left - _leftPanel.Padding.Right;

        for (int i = 0; i < _categories.Count; i++)
        {
            var category = _categories[i];
            var btn = new GlassButton
            {
                Text = category.Name,
                Font = CategoryFont,
                Size = new Size(btnWidth, 68),
                Margin = new Padding(0, 0, 0, 3),
                Cursor = Cursors.Default
            };

            SetupCategoryHoverEvents(btn, i);
            _categoryButtons.Add(btn);
            _leftPanel.Controls.Add(btn);
        }

        if (_categoryButtons.Count > 0)
        {
            SelectCategory(0);
        }
    }

    private void SetupCategoryHoverEvents(GlassButton categoryButton, int index)
    {
        categoryButton.MouseEnter += (s, e) =>
        {
            SelectCategory(index);
        };
    }

    private void SelectCategory(int index)
    {
        if (index < 0 || index >= _categories.Count)
            return;

        foreach (GlassButton btn in _categoryButtons)
            btn.IsSelected = false;

        _selectedCategoryIndex = index;
        _categoryButtons[index].IsSelected = true;

        _activeCategory = _categories[index];
        PopulateRightGrid(_categories[index].Items);
    }

    private void PopulateRightGrid(List<LauncherItem> items)
    {
        _rightPanel.Controls.Clear();

        if (items == null || items.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "No items in this category",
                ForeColor = ThemeHelper.IsDarkMode() ? Color.FromArgb(165, 165, 165) : Color.FromArgb(110, 110, 110),
                Font = EmptyFont,
                AutoSize = true,
                BackColor = _rightPanel.BackColor
            };
            _rightPanel.Controls.Add(emptyLabel);
            AutoSizeWindow();
            return;
        }

        foreach (var item in items)
        {
            var btn = new GlassButton
            {
                Text = item.Name,
                Font = ItemFont,
                Size = new Size(150, 44),
                Margin = new Padding(3, 3, 3, 3),
                Cursor = Cursors.Hand
            };

            btn.MouseEnter += (s, e) =>
            {
                _commandPreview.Text = FormatCommand(item.Path, item.Args);
            };
            btn.MouseLeave += (s, e) =>
            {
                _commandPreview.Text = "";
            };
            btn.Click += (s, e) =>
            {
                ExecuteScript(item.Path, item.Args);
                Application.Exit();
            };

            _rightPanel.Controls.Add(btn);
        }

        AutoSizeWindow();
    }

    private void AutoSizeWindow()
    {
        // Left panel height: padding + (count * (btnHeight + margin)) - lastMargin
        int leftHeight = _leftPanel.Padding.Top + _leftPanel.Padding.Bottom
            + _categoryButtons.Count * (68 + 3) - 3;

        // Right panel height: padding + rows * (btnHeight + topMargin + bottomMargin)
        int maxItems = _categories.Count > 0 ? _categories.Max(c => c.Items?.Count ?? 0) : 0;
        int cols = Math.Max(1, (_rightPanel.Width - _rightPanel.Padding.Left - _rightPanel.Padding.Right) / (150 + 6));
        int rows = (int)Math.Ceiling((double)maxItems / cols);
        int rightHeight = _rightPanel.Padding.Top + _rightPanel.Padding.Bottom + rows * (44 + 6);

        Height = Math.Max(leftHeight, rightHeight) + _commandPreview.Height;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_categoryButtons.Count == 0)
        {
            base.OnMouseWheel(e);
            return;
        }

        int direction = e.Delta > 0 ? -1 : 1;
        int newIndex = _selectedCategoryIndex + direction;

        if (newIndex >= 0 && newIndex < _categories.Count)
        {
            SelectCategory(newIndex);
            return;
        }

        base.OnMouseWheel(e);
    }

    public void ShowAtCursor()
    {
        Point cursor = Cursor.Position;
        const int offset = 40;

        int x = cursor.X - Width / 2;
        int y = cursor.Y - offset;

        Screen screen = Screen.FromPoint(cursor);
        if (x < screen.WorkingArea.Left) x = screen.WorkingArea.Left;
        if (y < screen.WorkingArea.Top) y = screen.WorkingArea.Top;
        if (x + Width > screen.WorkingArea.Right) x = screen.WorkingArea.Right - Width;
        if (y + Height > screen.WorkingArea.Bottom) y = screen.WorkingArea.Bottom - Height;

        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnFormClosing(e);
    }

    public static void ExecuteScript(string path, string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            Arguments = args,
            UseShellExecute = true
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

    private static void CreateDefaultConfig(string path)
    {
        var config = new AppConfig
        {
            Categories = new List<CategoryData>
            {
                new CategoryData
                {
                    Name = "LLMs (Heavy)",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Qwen 2.5 14B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Qwen 2.5 14B...'\"" },
                        new LauncherItem { Name = "Gemma 2 27B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Gemma 2 27B...'\"" },
                        new LauncherItem { Name = "Llama 3.1 70B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Llama 3.1 70B...'\"" },
                        new LauncherItem { Name = "Mixtral 8x7B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Mixtral 8x7B...'\"" },
                        new LauncherItem { Name = "Command R+", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Command R+...'\"" },
                        new LauncherItem { Name = "Yi 34B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Yi 34B...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "LLMs (Light)",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Mistral 7B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Mistral 7B...'\"" },
                        new LauncherItem { Name = "DeepSeek R1", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting DeepSeek R1...'\"" },
                        new LauncherItem { Name = "Phi-3 Mini", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Phi-3 Mini...'\"" },
                        new LauncherItem { Name = "Gemma 2 9B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Gemma 2 9B...'\"" },
                        new LauncherItem { Name = "Llama 3.2 3B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Llama 3.2 3B...'\"" },
                        new LauncherItem { Name = "Qwen 2.5 7B", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Qwen 2.5 7B...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "Embedding",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "nomic-embed", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting nomic-embed-text...'\"" },
                        new LauncherItem { Name = "bge-large", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting bge-large-en...'\"" },
                        new LauncherItem { Name = "text-embedding", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting text-embedding-3...'\"" },
                        new LauncherItem { Name = "all-MiniLM", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting all-MiniLM-L6...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "Image Gen",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Stable Diff.", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Stable Diffusion XL...'\"" },
                        new LauncherItem { Name = "Flux.1 Dev", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Flux.1 Dev...'\"" },
                        new LauncherItem { Name = "Flux.1 Schnell", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Flux.1 Schnell...'\"" },
                        new LauncherItem { Name = "SDXL Turbo", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting SDXL Turbo...'\"" },
                        new LauncherItem { Name = "DALL-E Local", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting DALL-E Local...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "Audio / Speech",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Whisper Large", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Whisper Large v3...'\"" },
                        new LauncherItem { Name = "Whisper Medium", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Whisper Medium...'\"" },
                        new LauncherItem { Name = "Faster-Whisp.", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Faster-Whisper...'\"" },
                        new LauncherItem { Name = "Bark TTS", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Bark TTS...'\"" },
                        new LauncherItem { Name = "Coqui TTS", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Coqui TTS...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "Data Utilities",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Terminal", Path = "wt.exe", Args = "" },
                        new LauncherItem { Name = "VS Code", Path = "code", Args = "." },
                        new LauncherItem { Name = "File Explorer", Path = "explorer", Args = "." },
                        new LauncherItem { Name = "Notepad++", Path = "notepad++", Args = "" },
                        new LauncherItem { Name = "SQLite Browser", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting DB Browser...'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "Dev Servers",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Ollama", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Ollama server...'\"" },
                        new LauncherItem { Name = "LM Studio", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting LM Studio...'\"" },
                        new LauncherItem { Name = "Text Gen Web", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting text-generation-webui...'\"" },
                        new LauncherItem { Name = "Open WebUI", Path = "powershell", Args = "-NoExit -Command \"Write-Host 'Starting Open WebUI...'\"" },
                        new LauncherItem { Name = "FastAPI Docs", Path = "powershell", Args = "-NoExit -Command \"Start 'http://localhost:8000/docs'\"" }
                    }
                },
                new CategoryData
                {
                    Name = "System Controls",
                    Items = new List<LauncherItem>
                    {
                        new LauncherItem { Name = "Settings", Path = "ms-settings:", Args = "" },
                        new LauncherItem { Name = "Task Manager", Path = "taskmgr.exe", Args = "" },
                        new LauncherItem { Name = "PowerShell", Path = "pwsh.exe", Args = "-NoExit" },
                        new LauncherItem { Name = "Cmd Prompt", Path = "cmd.exe", Args = "" },
                        new LauncherItem { Name = "Registry", Path = "regedit.exe", Args = "" },
                        new LauncherItem { Name = "Services", Path = "services.msc", Args = "" }
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(config, AppConfigContext.Default.AppConfig);
        File.WriteAllText(path, json);
    }
}
