using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using GpuVramMonitor;

namespace HeadlessGpuManager;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainManagerForm());
    }
}

public class MainManagerForm : Form
{
    private DataGridView _processGrid = null!;
    private NumericUpDown _ramFilterInput = null!;
    private CheckBox _gfxFilterCheckbox = null!;
    private Button _refreshBtn = null!;
    private Button _assignIgpuBtn = null!;
    private Button _assignDgpuBtn = null!;
    private Button _clearPrefBtn = null!;
    private Button _toggleKeeperBtn = null!;
    private Label _keeperStatusLabel = null!;
    private Label _vramLabel = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;

    private const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private string? _cachedKeeperPath;

    public MainManagerForm()
    {
        InitializeUiComponents();
        ConfigureStatusTimer();
        // Defer heavy work so the form appears immediately
        _ = Task.Run(LoadInitialData);
    }

    private void LoadInitialData()
    {
        try
        {
            UpdateVramDisplay();
            LoadProcessList();
        }
        catch (Exception ex)
        {
            if (!this.IsDisposed)
            {
                this.Invoke(() => MessageBox.Show($"Failed to load data: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
        }
    }

    private void InitializeUiComponents()
    {
        this.Text = "Advanced Hardware GPU Router & Daemon Controller";
        this.Size = new Size(1050, 650);
        this.MinimumSize = new Size(800, 500);
        this.StartPosition = FormStartPosition.CenterScreen;

        // Top Control Panel
        Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(240, 240, 240) };

        Label ramLabel = new Label { Text = "Min RAM (MB):", Top = 16, Left = 15, AutoSize = true };
        _ramFilterInput = new NumericUpDown { Top = 12, Left = 105, Width = 70, Minimum = 0, Maximum = 32000, Value = 50 };

        _gfxFilterCheckbox = new CheckBox { Text = "Graphics Engine Apps Only (DXGI/D3D/Vulkan)", Top = 14, Left = 190, AutoSize = true, Checked = false };
        _refreshBtn = new Button { Text = "Refresh Process Tree", Top = 10, Left = 520, Width = 150, Height = 28 };
        _refreshBtn.Click += (s, e) => _ = Task.Run(LoadProcessList);

        _vramLabel = new Label { Text = "VRAM: querying...", Top = 16, Left = 690, AutoSize = true, ForeColor = Color.DarkSlateGray };

        topPanel.Controls.AddRange(new Control[] { ramLabel, _ramFilterInput, _gfxFilterCheckbox, _refreshBtn, _vramLabel });

        // Bottom Operations Panel
        Panel bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 85, BackColor = Color.FromArgb(230, 235, 240) };

        GroupBox gpuGroup = new GroupBox { Text = "WDDM Hardware Routing Controls", Top = 5, Left = 15, Width = 520, Height = 70 };
        _assignIgpuBtn = new Button { Text = "Force to iGPU (Power Save)", Top = 25, Left = 15, Width = 160, Height = 30, Enabled = false };
        _assignDgpuBtn = new Button { Text = "Force to 7900 GRE (High Perf)", Top = 25, Left = 185, Width = 180, Height = 30, Enabled = false };
        _clearPrefBtn = new Button { Text = "Clear Rule", Top = 25, Left = 370, Width = 135, Height = 30, Enabled = false };

        _assignIgpuBtn.Click += (s, e) => ApplyGpuRule("GpuPreference=1;");
        _assignDgpuBtn.Click += (s, e) => ApplyGpuRule("GpuPreference=2;");
        _clearPrefBtn.Click += (s, e) => RemoveGpuRule();
        gpuGroup.Controls.AddRange(new Control[] { _assignIgpuBtn, _assignDgpuBtn, _clearPrefBtn });

        GroupBox keeperGroup = new GroupBox { Text = "HeadlessGpuKeeper Status", Top = 5, Left = 550, Width = 465, Height = 70 };
        _keeperStatusLabel = new Label { Text = "Detecting daemon state...", Top = 32, Left = 15, AutoSize = true, Font = new Font(this.Font, FontStyle.Bold) };
        _toggleKeeperBtn = new Button { Text = "Toggle Daemon", Top = 25, Left = 280, Width = 165, Height = 30 };
        _toggleKeeperBtn.Click += ToggleKeeperService;
        keeperGroup.Controls.AddRange(new Control[] { _keeperStatusLabel, _toggleKeeperBtn });

        bottomPanel.Controls.AddRange(new Control[] { gpuGroup, keeperGroup });

        // Center Grid Layout
        _processGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White
        };

        _processGrid.Columns.Add("ProcName", "Process Name");
        _processGrid.Columns.Add("PID", "PID");
        _processGrid.Columns.Add("RAM", "Working Set (MB)");
        _processGrid.Columns.Add("GfxHooks", "Graphics Modules?");
        _processGrid.Columns.Add("CurrentPref", "Active Registry Mapping");
        _processGrid.Columns.Add("Path", "Executable System Path");

        _processGrid.Columns["PID"].Width = 60;
        _processGrid.Columns["RAM"].Width = 110;
        _processGrid.Columns["GfxHooks"].Width = 120;
        _processGrid.SelectionChanged += OnGridSelectionChanged;

        this.Controls.AddRange(new Control[] { _processGrid, topPanel, bottomPanel });
    }

    private void UpdateVramDisplay()
    {
        var gpus = GpuVramReader.GetVramUsage();

        string text = gpus.Count == 0
            ? "VRAM: No physical GPUs detected"
            : "VRAM: " + string.Join("  |  ", gpus.Select(gpu =>
            {
                string vramStr = gpu.UsedMb >= 1024
                    ? $"{gpu.UsedMb / 1024.0:F1} GB"
                    : $"{gpu.UsedMb:F0} MB";
                return $"GPU {gpu.Index}: {vramStr}";
            }));

        this.Invoke(() => _vramLabel.Text = text);
    }

    private void ConfigureStatusTimer()
    {
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (s, e) =>
        {
            InspectKeeperProcessStatus();
            UpdateVramDisplay();
        };
        _statusTimer.Start();
    }

    private void LoadProcessList()
    {
        // Capture UI values on the UI thread before going to background
        long minRamBytes = 0;
        bool gfxOnly = false;
        this.Invoke((Action)(() =>
        {
            minRamBytes = (long)_ramFilterInput.Value * 1024 * 1024;
            gfxOnly = _gfxFilterCheckbox.Checked;
        }));

        // Gather data on background thread, update UI via Invoke
        var rows = CollectProcessRows(minRamBytes, gfxOnly);
        if (this.IsDisposed) return;
        this.Invoke(() =>
        {
            _processGrid.SuspendLayout();
            try
            {
                _processGrid.Rows.Clear();
                foreach (object[] row in rows)
                {
                    _processGrid.Rows.Add(row[0], row[1], row[2], row[3], row[4], row[5]);
                }
            }
            finally
            {
                _processGrid.ResumeLayout();
            }
        });
    }

    private object[] CollectProcessRows(long minRamBytes, bool gfxOnly)
    {
        // Cache registry key — open once, not per process
        using RegistryKey? regKey = Registry.CurrentUser.OpenSubKey(RegPath);

        var rows = new List<object[]>();
        var runningProcesses = Process.GetProcesses().OrderByDescending(p => p.WorkingSet64);

        foreach (var proc in runningProcesses)
        {
            if (proc.WorkingSet64 < minRamBytes) continue;

            string exePath = "Unknown (Access Denied)";
            bool mapsGraphics = false;

            try
            {
                exePath = proc.MainModule?.FileName ?? "Unknown";

                // Inspect process vtable hooks for graphics modules natively
                foreach (ProcessModule mod in proc.Modules)
                {
                    string modName = mod.ModuleName?.ToLower() ?? "";
                    if (modName is "dxgi.dll" or "d3d11.dll" or "d3d12.dll" or "vulkan-1.dll" or "opengl32.dll")
                    {
                        mapsGraphics = true;
                        break;
                    }
                }
            }
            catch { /* Catch cross-architecture access exceptions cleanly */ }

            if (gfxOnly && !mapsGraphics) continue;
            if (proc.Id == 0 || proc.Id == 4) continue; // Skip Idle and System

            string activeRegistryRule = "None (System Managed)";
            if (exePath != "Unknown (Access Denied)")
            {
                var val = regKey?.GetValue(exePath);
                if (val != null)
                {
                    activeRegistryRule = val.ToString() switch
                    {
                        "GpuPreference=1;" => "iGPU (Power Saving)",
                        "GpuPreference=2;" => "7900 GRE (High Perf)",
                        _ => val.ToString() ?? "Custom"
                    };
                }
            }

            rows.Add(new object[]
            {
                proc.ProcessName,
                proc.Id,
                (proc.WorkingSet64 / 1024 / 1024).ToString("N0"),
                mapsGraphics ? "YES" : "No",
                activeRegistryRule,
                exePath
            });
        }

        return rows.ToArray();
    }

    private void OnGridSelectionChanged(object? sender, EventArgs e)
    {
        if (_processGrid.SelectedRows.Count == 0)
        {
            ToggleActionButtons(false);
            return;
        }

        string path = _processGrid.SelectedRows[0].Cells["Path"].Value.ToString() ?? "";
        bool isValidPath = !string.IsNullOrEmpty(path) && !path.StartsWith("Unknown");
        ToggleActionButtons(isValidPath);
    }

    private void ToggleActionButtons(bool enabled)
    {
        _assignIgpuBtn.Enabled = enabled;
        _assignDgpuBtn.Enabled = enabled;
        _clearPrefBtn.Enabled = enabled;
    }

    private void ApplyGpuRule(string ruleValue)
    {
        if (_processGrid.SelectedRows.Count == 0) return;
        string exePath = _processGrid.SelectedRows[0].Cells["Path"].Value.ToString() ?? "";

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
        {
            key.SetValue(exePath, ruleValue, RegistryValueKind.String);
        }

        MessageBox.Show($"Successfully locked preference for:\n{Path.GetFileName(exePath)}\nChanges take effect next time the application launches.", "Registry Injected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        LoadProcessList();
    }

    private void RemoveGpuRule()
    {
        if (_processGrid.SelectedRows.Count == 0) return;
        string exePath = _processGrid.SelectedRows[0].Cells["Path"].Value.ToString() ?? "";

        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegPath, true))
        {
            key?.DeleteValue(exePath, false);
        }
        LoadProcessList();
    }

    private void InspectKeeperProcessStatus()
    {
        var keeperProcesses = Process.GetProcessesByName("HeadlessGpuKeeper");
        if (keeperProcesses.Length > 0)
        {
            _keeperStatusLabel.Text = $"Active (PID: {keeperProcesses[0].Id})";
            _keeperStatusLabel.ForeColor = Color.DarkGreen;
            _toggleKeeperBtn.Text = "Terminate Keeper";
        }
        else
        {
            _keeperStatusLabel.Text = "Stopped / Dormant";
            _keeperStatusLabel.ForeColor = Color.DarkRed;
            _toggleKeeperBtn.Text = "Launch Keeper Process";
        }
    }

    private void ToggleKeeperService(object? sender, EventArgs e)
    {
        var keeperProcesses = Process.GetProcessesByName("HeadlessGpuKeeper");
        if (keeperProcesses.Length > 0)
        {
            foreach (var proc in keeperProcesses)
            {
                try { proc.Kill(); } catch { }
            }
            Thread.Sleep(200);
            InspectKeeperProcessStatus();
        }
        else
        {
            // Scan paths to track down the keeper utility binary
            if (string.IsNullOrEmpty(_cachedKeeperPath) || !File.Exists(_cachedKeeperPath))
            {
                string localCheck = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HeadlessGpuKeeper.exe");
                string hardcodeCheck = @"C:\Users\tmxn\source\repos\gpukeepalive\HeadlessGPUKeeper\bin\Release\net8.0\HeadlessGPUKeeper.exe";

                if (File.Exists(localCheck)) _cachedKeeperPath = localCheck;
                else if (File.Exists(hardcodeCheck)) _cachedKeeperPath = hardcodeCheck;
                else
                {
                    using OpenFileDialog ofd = new OpenFileDialog
                    {
                        Filter = "Executable Files (*.exe)|*.exe",
                        Title = "Locate HeadlessGpuKeeper.exe for Service Binding"
                    };
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        _cachedKeeperPath = ofd.FileName;
                    }
                    else return;
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _cachedKeeperPath,
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
                Thread.Sleep(200);
                InspectKeeperProcessStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not spin up daemon: {ex.Message}", "Process Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
