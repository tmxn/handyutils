using System.Drawing;
using System.Runtime.InteropServices;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HeadlessGpuKeeper;

/// <summary>
/// A tiny borderless, topmost, single-row window shown only while the keeper is in
/// active mode (llama-server running). Clicking the VRAM text opens a quick menu to
/// pick which GPU to monitor; the text shows used VRAM and the bar shows total load.
/// Position and selected GPU index are remembered in the registry.
/// </summary>
public sealed class GpuMonitorForm : Form
{
    const string RegPath = @"Software\HeadlessGPUKeeper";
    const string LlamaBase = "http://localhost:8080";
    const string DefaultModelText = "Loading Model..";
    const string DefaultCtxText = "..../..";

    // Linger (after llama-server exits) until the adapter's dedicated usage is back
    // at this idle baseline plus a short tail, so the freed VRAM is actually visible.
    const double LingerVramLimitMb = KeeperCore.IdlePokeVramLimitMb;
    const int LingerTailSeconds = 5;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    readonly KeeperCore _core;
    readonly System.Windows.Forms.Timer _timer;
    readonly HttpClient _llamaClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    ContextMenuStrip _gpuMenu = null!;
    Label _vramLabel = null!;
    Label _modelLabel = null!;
    Label _ctxLabel = null!;
    ProgressBar _loadBar = null!;
    string? _modelName;
    long _lastCtx = -1;
    bool _polling;

    GpuInfo[] _gpus = Array.Empty<GpuInfo>();
    GpuMonitor? _monitor;
    int _selectedIndex = -1;
    bool _isActive;
    bool _readyToShow;
    DateTime _lingerUntil;
    bool _dragging;
    bool _wasAtBaseline;
    bool _wasIdlePoking;
    Point _dragOffset;

    public GpuMonitorForm()
    {
        _core = new KeeperCore();

        BuildUi();
        LoadSettings();
        PopulateGpuList();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // Suppress the initial show so the window stays hidden until the first active tick.
    protected override void SetVisibleCore(bool value)
        => base.SetVisibleCore(_readyToShow && value);

    void BuildUi()
    {
        SuspendLayout();

        Text = "Headless GPUKeeper";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(32, 32, 32);
        Font = new Font("Segoe UI", 8F);

        _gpuMenu = new ContextMenuStrip();
        _vramLabel = new Label
        {
            AutoSize = false,
            Width = 63,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8F)
        };
        _vramLabel.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _gpuMenu.Show(Cursor.Position);
        };

        // Model file name: small font so most names fit on two rows of the 26px height.
        _modelLabel = new Label
        {
            AutoSize = false,
            Width = 64,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(64, 0),
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 6F),
            Text = DefaultModelText
        };

        _ctxLabel = new Label
        {
            AutoSize = false,
            Width = 102,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(129, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8F),
            Text = DefaultCtxText
        };

        _loadBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 61,
            Height = 24,
            Location = new Point(232, 1),
            Style = ProgressBarStyle.Continuous
        };

        Controls.AddRange(new Control[] { _vramLabel, _modelLabel, _ctxLabel, _loadBar });

        // Hug the content exactly: fixed 26px row height, form trimmed to the content.
        // The progress bar is inset by 1px on every side so it reads smaller than the text.
        int h = 26;
        _vramLabel.Height = h;
        _modelLabel.Height = h;
        _ctxLabel.Height = h;
        _loadBar.Height = 24;
        ClientSize = new Size(_loadBar.Right + 1, h);

        AttachDrag(this);
        AttachDrag(_loadBar);
        AttachDrag(_modelLabel);
        AttachDrag(_ctxLabel);

        ResumeLayout();
    }

    void AttachDrag(Control c)
    {
        c.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragOffset = Cursor.Position - (Size)Location;
        };
        c.MouseMove += (s, e) =>
        {
            if (_dragging) Location = Cursor.Position - (Size)_dragOffset;
        };
        c.MouseUp += (s, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            SavePosition();
        };
    }

    void LoadSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            int x = key?.GetValue("X") as int? ?? -1;
            int y = key?.GetValue("Y") as int? ?? -1;
            // Reject saved positions that are no longer on any screen (e.g. the
            // monitor resolution dropped), so the borderless window is reachable.
            if (x >= 0 && y >= 0 && OnAnyWorkingArea(new Point(x, y)))
            {
                Location = new Point(x, y);
            }
            else
            {
                var wa = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetBounds(this);
                Location = new Point(wa.Right - Width, wa.Top);
            }
        }
        catch
        {
            Location = new Point(Screen.PrimaryScreen!.WorkingArea.Right - Width, Screen.PrimaryScreen.WorkingArea.Top);
        }
    }

    static bool OnAnyWorkingArea(Point p)
        => Screen.AllScreens.Any(s => s.WorkingArea.Contains(p));

    void SavePosition()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue("X", Location.X, RegistryValueKind.DWord);
            key.SetValue("Y", Location.Y, RegistryValueKind.DWord);
        }
        catch { }
    }

    void PopulateGpuList()
    {
        _gpus = GpuMonitor.EnumerateGpus();
        _gpuMenu.Items.Clear();
        foreach (var g in _gpus)
        {
            var item = new ToolStripMenuItem($"GPU {g.Index}") { Tag = g.Index };
            item.Click += (s, e) => ApplyGpuSelection((int)((ToolStripMenuItem)s!).Tag!);
            _gpuMenu.Items.Add(item);
        }

        int saved = -1;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            saved = key?.GetValue("Gpu") as int? ?? -1;
        }
        catch { }

        if (saved < 0 || saved >= _gpus.Length)
            saved = _gpus.Length > 0 ? 0 : -1;

        ApplyGpuSelection(saved);
    }

    void ApplyGpuSelection(int index)
    {
        _selectedIndex = index;
        _monitor?.Dispose();
        _monitor = index >= 0 && index < _gpus.Length
            ? new GpuMonitor(_gpus[index].Luid)
            : null;

        if (_isActive) _monitor?.Enable();

        foreach (ToolStripMenuItem item in _gpuMenu.Items)
            item.Checked = (int)item.Tag! == index;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue("Gpu", index, RegistryValueKind.DWord);
        }
        catch { }
    }

    void OnTick(object? sender, EventArgs e)
    {
        _readyToShow = true;
        bool wasShowing = _isActive;

        // Only the UI reads VRAM/load, and only while visible: the PDH counters
        // are torn down the moment we hide. Sample before the keeper tick so the
        // post-exit poke suppression can use the latest usage.
        double vramMb = 0, loadPercent = 0;
        double? coreVram = null;
        if (wasShowing)
        {
            _monitor?.Enable();
            (vramMb, loadPercent) = _monitor?.Sample() ?? (0, 0);
            coreVram = vramMb;
            // Poll llama-server alongside the VRAM read; stops when the form hides.
            if (!_polling)
                _ = PollLlamaAsync();
        }

        bool active = _core.Tick(coreVram) == KeeperCore.ModeActive;

        // Stay visible while llama-server runs, and after it exits until the
        // adapter's usage is back at the idle baseline plus a short tail.
        // Arm the tail once, on whichever comes first: usage dropping to baseline,
        // or the keeper firing its first idle poke. Never refresh it afterwards:
        // the poke itself allocates >100 MB, so usage-based checks would keep the
        // UI up forever.
        bool atBaseline = vramMb <= LingerVramLimitMb;
        bool idlePoking = _core.IdlePoking;
        if ((atBaseline && !_wasAtBaseline) || (idlePoking && !_wasIdlePoking))
            _lingerUntil = DateTime.UtcNow.AddSeconds(LingerTailSeconds);
        _wasAtBaseline = atBaseline;
        _wasIdlePoking = idlePoking;

        // Once idle poking has started the tail is the only thing keeping the UI
        // up (poke VRAM spikes must not extend it).
        bool tailRunning = DateTime.UtcNow < _lingerUntil;
        bool showing = active || (wasShowing && (idlePoking ? tailRunning : !atBaseline || tailRunning));
        _isActive = showing;

        if (showing)
        {
            Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            _vramLabel.Text = FormatVram(vramMb);
            _loadBar.Value = (int)Math.Round(loadPercent);
        }
        else
        {
            _monitor?.Disable();
            Hide();
        }
    }

    async Task PollLlamaAsync()
    {
        _polling = true;
        try
        {
            var slots = await _llamaClient.GetFromJsonAsync<JsonElement[]>(LlamaBase + "/slots");
            if (slots is { Length: > 0 })
            {
                var slot = slots[0];
                long ctx = slot.TryGetProperty("n_ctx", out var c) ? c.GetInt64() : 0;
                long used = slot.TryGetProperty("n_prompt_tokens", out var t) ? t.GetInt64() : 0;
                if (ctx > 0)
                {
                    _ctxLabel.Text = $"{FormatK(used)}/{FormatK(ctx)}";

                    // A different total context means a new model was loaded: refresh the name.
                    bool modelChanged = ctx != _lastCtx;
                    if (modelChanged)
                        _lastCtx = ctx;

                    if (_modelName is null or "" || modelChanged)
                        _modelName = await FetchModelNameAsync() ?? _modelName ?? "";

                    if (_modelName.Length > 0)
                        _modelLabel.Text = _modelName;
                }
            }
        }
        catch
        {
            // llama-server is gone (the form may linger for a while): drop the stale info.
            _modelName = null;
            _lastCtx = -1;
            _modelLabel.Text = DefaultModelText;
            _ctxLabel.Text = DefaultCtxText;
        }
        finally
        {
            _polling = false;
        }
    }

    // Returns the model file name, "" if /props is reachable but has no usable
    // model_path (so it isn't re-fetched), or null if the server is unreachable.
    async Task<string?> FetchModelNameAsync()
    {
        JsonElement props;
        try
        {
            props = await _llamaClient.GetFromJsonAsync<JsonElement>(LlamaBase + "/props");
        }
        catch
        {
            return null;
        }

        if (props.TryGetProperty("model_path", out var mp) && mp.ValueKind == JsonValueKind.String && mp.GetString() is { } path)
            return Path.GetFileName(path);
        return "";
    }

    static string FormatVram(double mb)
        => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";

    // 950 -> "950", 6500 -> "6.5k", 128000 -> "128k"
    static string FormatK(long v)
        => v < 1000 ? v.ToString() : $"{(double)v / 1000.0:0.##}k";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Re-hug after WinForms' first-show layout may have inflated the form size.
        int h = 26;
        _vramLabel.Height = h;
        _modelLabel.Height = h;
        _ctxLabel.Height = h;
        _loadBar.Location = new Point(232, 1);
        _loadBar.Height = 24;
        var sz = new Size(_loadBar.Right + 1, h);
        MinimumSize = sz;
        MaximumSize = sz;
        ClientSize = sz;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _gpuMenu.Dispose();
            _monitor?.Dispose();
        }
        base.Dispose(disposing);
    }
}
