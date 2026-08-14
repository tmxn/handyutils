using System.Diagnostics;

namespace HeadlessGpuKeeper;

/// <summary>
/// Identifies a single GPU adapter exposed through the "GPU Adapter Memory"
/// performance counter category (one instance per adapter, virtual ones included).
/// </summary>
public readonly struct GpuInfo
{
    public int Index { get; init; }
    public string Luid { get; init; }
}

/// <summary>
/// Reads dedicated VRAM usage and total GPU load for one selected adapter.
///
/// VRAM comes from the "GPU Adapter Memory" category ("Dedicated Usage"), the same
/// API PreferredGPUChanger uses. Total load comes from the "GPU Engine" category
/// ("Utilization Percentage"), summed across every engine belonging to the selected
/// adapter's LUID. Counters are only created while enabled so that in idle mode no
/// CPU cycles are spent polling them.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly string _luid;
    private PerformanceCounter? _memory;
    private readonly List<PerformanceCounter> _engines = new();

    public bool Enabled { get; private set; }

    public GpuMonitor(string luid) => _luid = luid;

    /// <summary>
    /// Enumerates every GPU adapter instance (physical and virtual) in a stable order.
    /// </summary>
    public static GpuInfo[] EnumerateGpus()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            var list = category.GetInstanceNames()
                .Where(n => n.Contains("_phys_0"))
                .Select(n => new GpuInfo { Luid = ExtractLuid(n) })
                .Where(g => g.Luid.Length > 0)
                .OrderBy(g => g.Luid, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < list.Count; i++)
                list[i] = list[i] with { Index = i };

            return list.ToArray();
        }
        catch
        {
            return Array.Empty<GpuInfo>();
        }
    }

    public void Enable()
    {
        if (Enabled) return;
        Enabled = true;

        try
        {
            var memCat = new PerformanceCounterCategory("GPU Adapter Memory");
            string? memInstance = memCat.GetInstanceNames().FirstOrDefault(n => n.Contains(_luid, StringComparison.Ordinal));
            if (memInstance != null)
            {
                _memory = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", memInstance, true);
                _memory.NextValue();
            }

            var engineCat = new PerformanceCounterCategory("GPU Engine");
            foreach (string inst in engineCat.GetInstanceNames())
            {
                if (!inst.Contains(_luid, StringComparison.Ordinal)) continue;
                var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                c.NextValue();
                _engines.Add(c);
            }
        }
        catch
        {
            // Counters unavailable; reads will simply report zero.
        }
    }

    public void Disable()
    {
        if (!Enabled) return;
        Enabled = false;

        _memory?.Dispose();
        _memory = null;

        foreach (var c in _engines) c.Dispose();
        _engines.Clear();
    }

    public double VramMb
    {
        get
        {
            try
            {
                if (_memory == null) return 0;
                float bytes = _memory.NextValue();
                return bytes < 0 ? 0 : bytes / (1024.0 * 1024.0);
            }
            catch { return 0; }
        }
    }

    public double LoadPercent
    {
        get
        {
            double total = 0;
            foreach (var c in _engines)
            {
                try { total += Math.Max(0, c.NextValue()); }
                catch { }
            }
            return Math.Min(100, total);
        }
    }

    public void Dispose() => Disable();

    private static string ExtractLuid(string instance)
    {
        int s = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (s < 0) return "";
        int e = instance.IndexOf("_phys_", s, StringComparison.Ordinal);
        return e > s ? instance.Substring(s, e - s) : "";
    }
}
