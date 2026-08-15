using System.Diagnostics;
using System.Runtime.InteropServices;

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
/// ("Utilization Percentage"), summed across every engine instance belonging to the
/// selected adapter's LUID (3D, Copy, Video Decode, Compute, ...), so the LLM load
/// on the compute engines is included.
///
/// The "GPU Engine" category only exposes per-process instances
/// (pid_..._luid_..._phys_0_eng_N_engtype_X), so a busy system can have hundreds of
/// them. Instead of one PerformanceCounter per instance (each with its own PDH
/// query, ~200 ms/s to sample on this machine), all counters are added to a single
/// shared PDH query: one PdhCollectQueryData per sample (~3 ms/s). The instance set
/// is re-enumerated every 30 s so engines of processes that start or stop after
/// Enable() are picked up. Counters are only created while enabled so that in idle
/// mode no CPU cycles are spent polling them.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    const string MemoryCategory = "GPU Adapter Memory";
    const string EngineCategory = "GPU Engine";
    const int InstanceRefreshMs = 30_000;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhOpenQuery(string? szDataSource, ulong dwMachineName, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, ulong dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PdhCounterValue pValue);

    [DllImport("pdh.dll")]
    static extern int PdhCloseQuery(IntPtr hQuery);

    // Native PDH_FMT_COUNTERVALUE: DWORD CStatus + 8-byte-aligned union (16 bytes on x64).
    [StructLayout(LayoutKind.Sequential)]
    struct PdhCounterValue
    {
        public uint CStatus;
        public double Value; // union slot at offset 8
    }

    const uint PdhFmtDouble = 0x00000200; // PDH_FMT_DOUBLE
    const uint PdhCstatusValidData = 0;

    readonly string _luid;
    IntPtr _query;
    IntPtr _memoryCounter;
    readonly List<IntPtr> _engineCounters = new();
    readonly List<string> _engineInstances = new();
    DateTime _lastRefreshUtc;

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
        if (!TryRebuild(EnumerateEngineInstances()))
            return; // stays disabled; the caller retries on the next tick
        Enabled = true;
    }

    public void Disable()
    {
        if (!Enabled) return;
        Enabled = false;
        CloseQuery();
    }

    /// <summary>
    /// Samples VRAM usage (MB) and total load (0-100) in one PDH collect pass.
    /// </summary>
    public (double VramMb, double LoadPercent) Sample()
    {
        if (!Enabled || _query == IntPtr.Zero)
            return (0, 0);

        if ((DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds >= InstanceRefreshMs)
            RefreshInstances();

        try
        {
            PdhCollectQueryData(_query);
        }
        catch
        {
            return (0, 0);
        }

        double vramMb = 0;
        if (_memoryCounter != IntPtr.Zero &&
            PdhGetFormattedCounterValue(_memoryCounter, PdhFmtDouble, out _, out PdhCounterValue memoryValue) == 0 &&
            memoryValue.CStatus == PdhCstatusValidData &&
            memoryValue.Value >= 0)
        {
            vramMb = memoryValue.Value / (1024.0 * 1024.0);
        }

        double load = 0;
        foreach (IntPtr counter in _engineCounters)
        {
            // Dead per-process instances report a non-valid CStatus; treat them as 0.
            if (PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out PdhCounterValue engineValue) == 0 &&
                engineValue.CStatus == PdhCstatusValidData &&
                engineValue.Value > 0)
            {
                load += engineValue.Value;
            }
        }

        return (vramMb, Math.Min(100, load));
    }

    public void Dispose() => Disable();

    List<string> EnumerateEngineInstances()
    {
        try
        {
            var category = new PerformanceCounterCategory(EngineCategory);
            var list = new List<string>();
            foreach (string instance in category.GetInstanceNames())
                if (instance.Contains(_luid, StringComparison.Ordinal))
                    list.Add(instance);
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    void RefreshInstances()
    {
        _lastRefreshUtc = DateTime.UtcNow;

        List<string> current;
        try
        {
            current = EnumerateEngineInstances();
        }
        catch
        {
            return; // keep the existing query; retry on the next refresh
        }

        if (SameInstanceList(current, _engineInstances))
            return;

        TryRebuild(current);
    }

    bool TryRebuild(List<string> engineInstances)
    {
        CloseQuery();
        try
        {
            if (PdhOpenQuery(null, 0, out _query) != 0)
            {
                _query = IntPtr.Zero;
                return false;
            }

            _memoryCounter = IntPtr.Zero;
            string? memoryInstance = null;
            try
            {
                var memoryCategory = new PerformanceCounterCategory(MemoryCategory);
                memoryInstance = memoryCategory.GetInstanceNames()
                    .FirstOrDefault(n => n.Contains(_luid, StringComparison.Ordinal));
            }
            catch { }

            if (memoryInstance != null &&
                PdhAddEnglishCounter(_query, $@"\{MemoryCategory}({memoryInstance})\Dedicated Usage", 0, out _memoryCounter) != 0)
            {
                _memoryCounter = IntPtr.Zero;
            }

            _engineInstances.Clear();
            _engineCounters.Clear();
            foreach (string instance in engineInstances)
            {
                // The instance may vanish between enumeration and add; skip it.
                if (PdhAddEnglishCounter(_query, $@"\{EngineCategory}({instance})\Utilization Percentage", 0, out IntPtr counter) == 0)
                {
                    _engineInstances.Add(instance);
                    _engineCounters.Add(counter);
                }
            }

            PdhCollectQueryData(_query); // prime the rate counters
            _lastRefreshUtc = DateTime.UtcNow;
            return true;
        }
        catch
        {
            CloseQuery();
            return false;
        }
    }

    void CloseQuery()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { }
            _query = IntPtr.Zero;
        }
        _memoryCounter = IntPtr.Zero;
        _engineCounters.Clear();
        _engineInstances.Clear();
    }

    static bool SameInstanceList(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string ExtractLuid(string instance)
    {
        int s = instance.IndexOf("luid_", StringComparison.Ordinal);
        if (s < 0) return "";
        int e = instance.IndexOf("_phys_", s, StringComparison.Ordinal);
        return e > s ? instance.Substring(s, e - s) : "";
    }
}
