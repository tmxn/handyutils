using System.Drawing;
using System.Runtime.InteropServices;
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

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    readonly KeeperCore _core;
    readonly System.Windows.Forms.Timer _timer;

    ContextMenuStrip _gpuMenu = null!;
    Label _vramLabel = null!;
    ProgressBar _loadBar = null!;

    GpuInfo[] _gpus = Array.Empty<GpuInfo>();
    GpuMonitor? _monitor;
    int _selectedIndex = -1;
    bool _isActive;
    bool _readyToShow;
    DateTime _lingerUntil;
    bool _dragging;
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

        _loadBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 61,
            Height = 19,
            Location = new Point(64, 1),
            Style = ProgressBarStyle.Continuous
        };

        Controls.AddRange(new Control[] { _vramLabel, _loadBar });

        // Hug the content exactly: fixed 21px row height, form trimmed to the content.
        // The progress bar is inset by 1px on every side so it reads smaller than the text.
        int h = 21;
        _vramLabel.Height = h;
        _loadBar.Height = 19;
        ClientSize = new Size(_loadBar.Right + 1, h);

        AttachDrag(this);
        AttachDrag(_loadBar);

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
            if (x >= 0 && y >= 0)
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
        bool active = _core.Tick() == KeeperCore.ModeActive;
        if (active) _lingerUntil = DateTime.UtcNow.AddSeconds(10);

        // Stay visible for 10s after llama-server exits before hiding.
        bool showing = active || DateTime.UtcNow < _lingerUntil;
        _isActive = showing;

        if (showing)
        {
            _monitor?.Enable();
            Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            var (vramMb, loadPercent) = _monitor?.Sample() ?? (0, 0);
            _vramLabel.Text = FormatVram(vramMb);
            _loadBar.Value = (int)Math.Round(loadPercent);
        }
        else
        {
            _monitor?.Disable();
            Hide();
        }
    }

    static string FormatVram(double mb)
        => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Re-hug after WinForms' first-show layout may have inflated the form size.
        int h = 21;
        _vramLabel.Height = h;
        _loadBar.Location = new Point(64, 1);
        _loadBar.Height = 19;
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
