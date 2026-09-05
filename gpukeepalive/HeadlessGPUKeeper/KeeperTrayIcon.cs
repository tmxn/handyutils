using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HeadlessGpuKeeper;

/// <summary>
/// The keeper's only persistent UI. It runs hidden almost all the time — the VRAM
/// overlay appears only while llama-server is up — so without this there is no way to
/// see what it is doing, edit its rules, or quit it short of Task Manager.
/// </summary>
public sealed class KeeperTrayIcon : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly ToolStripMenuItem _rulesMenu;
    readonly ToolStripMenuItem _autoStartItem;
    readonly DynamicPinWatcher _watcher;
    readonly ConcurrentQueue<string> _pending = new();
    readonly IntPtr _iconHandle;

    public KeeperTrayIcon(DynamicPinWatcher watcher)
    {
        _watcher = watcher;

        _rulesMenu = new ToolStripMenuItem("Re-pinning dynamic apps");
        _autoStartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _autoStartItem.Click += OnToggleAutoStart;

        var repinNow = new ToolStripMenuItem("Re-pin dynamic apps now");
        repinNow.Click += OnRepinNow;

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Application.Exit();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            repinNow,
            _rulesMenu,
            new ToolStripSeparator(),
            _autoStartItem,
            new ToolStripSeparator(),
            exit
        });
        // Match counts and the autostart state go stale while the menu is closed, so
        // refresh them at the moment it is opened rather than on a timer.
        _menu.Opening += (_, _) => RefreshMenu();

        (Icon icon, IntPtr handle) = BuildIcon();
        _iconHandle = handle;

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "HeadlessGPUKeeper",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += OnRepinNow;

        _watcher.Changed += OnWatcherChanged;
        RefreshMenu();
    }

    void OnRepinNow(object? sender, EventArgs e)
    {
        SyncReport report = _watcher.SyncNow();
        string message = report.Changed
            ? $"Pinned {report.Added.Count}, pruned {report.Removed.Count}."
            : "Everything already pinned correctly.";
        _icon.ShowBalloonTip(3000, "HeadlessGPUKeeper", message, ToolTipIcon.Info);
    }

    /// <summary>
    /// Watcher callbacks arrive on the thread pool, and NotifyIcon is UI-affine. Queue
    /// the text here and let <see cref="PumpPending"/> surface it from the form's timer,
    /// which is the one place guaranteed to be on the UI thread.
    /// </summary>
    void OnWatcherChanged(SyncReport report)
    {
        string message;
        if (report.Added.Count > 0)
        {
            string names = string.Join(
                Environment.NewLine,
                report.Added.Select(Path.GetFileName).Distinct().Take(3));
            message = $"Re-pinned after an update:{Environment.NewLine}{names}";
        }
        else
        {
            message = $"Pruned {report.Removed.Count} stale entr{(report.Removed.Count == 1 ? "y" : "ies")}.";
        }

        _pending.Enqueue(message);
    }

    /// <summary>Must be called on the UI thread. Drains queued notifications.</summary>
    public void PumpPending()
    {
        while (_pending.TryDequeue(out string? message))
        {
            _icon.ShowBalloonTip(4000, "HeadlessGPUKeeper", message, ToolTipIcon.Info);
        }
    }

    void RefreshMenu()
    {
        _rulesMenu.DropDownItems.Clear();

        foreach (PinRule rule in _watcher.Rules.Rules)
        {
            int matches = RePinner.Expand(rule.ExpandedFilter).Count;
            string target = rule.Preference == 2 ? "dGPU" : "iGPU";
            string label = $"{rule.Filter}   ({matches} matched → {target})";

            var item = new ToolStripMenuItem(label)
            {
                Checked = rule.Enabled,
                ToolTipText = $"{rule.Name}{Environment.NewLine}Click to open the folder being watched.",
                Tag = rule
            };
            item.Click += OnOpenRuleFolder;
            _rulesMenu.DropDownItems.Add(item);
        }

        if (_rulesMenu.DropDownItems.Count == 0)
        {
            _rulesMenu.DropDownItems.Add(new ToolStripMenuItem("No rules configured") { Enabled = false });
        }

        _rulesMenu.DropDownItems.Add(new ToolStripSeparator());
        // Deliberately inert: it is a signpost to the file the user edits by hand. The
        // watcher picks the edit up without a restart.
        _rulesMenu.DropDownItems.Add(new ToolStripMenuItem($"Config: {PinRuleSet.ConfigPath}") { Enabled = false });

        _autoStartItem.Checked = AutoStart.IsInstalled();

        string last = RePinner.LastSyncUtc == default
            ? "not yet run"
            : RePinner.LastSyncUtc.ToLocalTime().ToString("HH:mm:ss");
        // NotifyIcon.Text is capped at 63 characters.
        _icon.Text = Truncate($"HeadlessGPUKeeper - last sync {last}", 63);
    }

    void OnOpenRuleFolder(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: PinRule rule }) return;

        string folder = rule.WatchRoot;
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            folder = Path.GetDirectoryName(folder) ?? "";
        }
        if (string.IsNullOrEmpty(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { }
    }

    void OnToggleAutoStart(object? sender, EventArgs e)
    {
        (bool ok, string message) = AutoStart.IsInstalled()
            ? AutoStart.Uninstall()
            : AutoStart.Install();

        _autoStartItem.Checked = AutoStart.IsInstalled();
        _icon.ShowBalloonTip(3000, "HeadlessGPUKeeper", message, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>
    /// Draws the tray icon rather than shipping a .ico, so the project stays a
    /// single-output build with no content files.
    /// </summary>
    static (Icon, IntPtr) BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            using var background = new SolidBrush(Color.FromArgb(32, 36, 40));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var ring = new Pen(Color.FromArgb(80, 200, 160), 2.5f);
            g.DrawEllipse(ring, 2, 2, 27, 27);

            using var font = new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            using var centre = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("G", font, text, new RectangleF(0, 0, 32, 32), centre);
        }

        IntPtr handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        _watcher.Changed -= OnWatcherChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
    }
}
