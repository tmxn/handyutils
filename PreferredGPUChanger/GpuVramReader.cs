using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GpuVramMonitor;

/// <summary>
/// Represents a single physical GPU with its dedicated VRAM usage.
/// </summary>
public readonly struct GpuVramInfo
{
    public int Index { get; init; }
    public string Name { get; init; }
    public double UsedMb { get; init; }
}

/// <summary>
/// Queries dedicated VRAM usage per physical GPU via Windows Performance Counters,
/// filtering out virtual/display-only GPUs (Parsec, USB monitors, Remote Desktop, etc.).
///
/// Constraint-compliant: uses only WMI (Win32_VideoController) and the
/// "GPU Adapter Memory" performance counter category. No registry, no DXGI.
///
/// Filtering strategy
/// ------------------
/// The "GPU Adapter Memory" category exposes one "phys_0" instance per adapter (virtual
/// display adapters included). Genuine physical GPUs hold real video memory (committed in
/// the GB range), whereas virtual display adapters allocate almost none (KB range). We:
///   1. Get the authoritative count of physical GPUs from WMI (PCI\ PNP + present AdapterRAM).
///   2. Iterate all phys_0 perf-counter instances and sort by committed memory descending.
///   3. Keep only the top N instances (N = physical GPU count from WMI), dropping virtuals.
/// </summary>
public static class GpuVramReader
{
    // Lazily-built state: one usage counter per physical GPU, plus a friendly name.
    private static List<(PerformanceCounter Usage, string Name)>? _gpus;
    private static bool _initialized;
    // Adapter instance names (e.g. "luid_0x00000000_0x834815e3_phys_0") for the
    // physical GPUs selected above, kept so the discrete GPU's LUID can be recovered.
    private static string[] _selectedInstances = [];

    private static readonly Regex InstanceLuidRegex = new(
        @"^luid_(0x[0-9a-f]+)_(0x[0-9a-f]+)_phys_", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns dedicated VRAM usage for each physical GPU (filtered, virtual GPUs excluded).
    /// </summary>
    public static List<GpuVramInfo> GetVramUsage()
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_gpus == null || _gpus.Count == 0)
        {
            return [];
        }

        var results = new List<GpuVramInfo>();
        for (int i = 0; i < _gpus!.Count; i++)
        {
            float usageBytes = _gpus[i].Usage.NextValue();
            results.Add(new GpuVramInfo
            {
                Index = i,
                Name = _gpus[i].Name,
                UsedMb = usageBytes / (1024.0 * 1024.0)
            });
        }

        return results;
    }

    private static void Initialize()
    {
        try
        {
            _gpus = BuildPhysicalGpuList();
        }
        catch
        {
            _gpus = null;
        }

        _initialized = true;
    }

    /// <summary>
    /// Cross-references WMI physical GPU metadata with the perf-counter adapter instances,
    /// returning one (usage counter, name) tuple per physical GPU.
    /// </summary>
    private static List<(PerformanceCounter, string)>? BuildPhysicalGpuList()
    {
        // 1. Authoritative physical GPUs from WMI.
        int physicalGpuCount = CountPhysicalGpusFromWmi();
        if (physicalGpuCount == 0)
        {
            return null;
        }

        // 2. Iterate all phys_0 instances in the perf-counter category.
        var category = new PerformanceCounterCategory("GPU Adapter Memory");
        string[] physInstances = category.GetInstanceNames()
            .Where(n => n.EndsWith("phys_0"))
            .ToArray();

        // 3. Sort by total dedicated VRAM capacity descending and keep the top N.
        //    (Virtual display adapters have essentially no capacity, so they fall to the
        //    bottom — and this is immune to the "idle dGPU looks like the smallest
        //    committed adapter" problem the committed-only sort suffered from.)
        var selected = physInstances
            .Select(inst => (Instance: inst, Capacity: ReadTotalCapacityBytes(inst)))
            .OrderByDescending(x => x.Capacity)
            .Take(physicalGpuCount)
            .ToList();

        _selectedInstances = selected.Select(x => x.Instance).ToArray();

        // 4. Wrap each selected instance's usage counter with a friendly display name.
        var result = new List<(PerformanceCounter, string)>();
        for (int i = 0; i < selected.Count; i++)
        {
            var usage = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", selected[i].Instance, true);
            usage.NextValue(); // prime the counter
            result.Add((usage, $"GPU {i}"));
        }

        return result;
    }

    /// <summary>
    /// Returns the LUID pair (low, high) of the discrete GPU, identified as the physical
    /// adapter with the largest total VRAM capacity — the same criterion the VRAM label
    /// uses to order the adapters. Ranking by capacity (not "Total Committed") avoids
    /// picking a busy iGPU over an idle discrete GPU. Null if no physical GPU detected.
    /// </summary>
    public static (string Low, string High)? GetDiscreteGpuLuid()
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_selectedInstances.Length == 0)
        {
            return null;
        }

        string bestInstance = _selectedInstances
            .OrderByDescending(inst => ReadTotalCapacityBytes(inst))
            .First();

        var m = InstanceLuidRegex.Match(bestInstance);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// <summary>
    /// Returns the LUID pair (low, high) for every physical (dedicated-VRAM) adapter
    /// selected at init. Used so the process reader can list dedicated-VRAM consumers
    /// across the real GPUs without having to guess which single one is the "discrete" GPU
    /// at any given instant (an idle discrete GPU's counters read low, so a committed-based
    /// pick mis-fires and the list comes back empty).
    /// </summary>
    public static IEnumerable<(string Low, string High)> GetPhysicalGpuLuids()
    {
        if (!_initialized)
        {
            Initialize();
        }

        foreach (var inst in _selectedInstances)
        {
            var m = InstanceLuidRegex.Match(inst);
            if (m.Success)
            {
                yield return (m.Groups[1].Value, m.Groups[2].Value);
            }
        }
    }

    /// <summary>
    /// Estimates an adapter's total dedicated VRAM capacity (bytes): the currently used
    /// "Dedicated Usage" plus the currently free "Dedicated Memory". Both together are
    /// essentially usage-independent, unlike "Total Committed" which collapses toward
    /// zero when the adapter is idle (and would let a busy iGPU outrank an idle discrete
    /// GPU when ranking by committed memory).
    /// </summary>
    private static long ReadTotalCapacityBytes(string instance)
    {
        try
        {
            var used = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, true);
            used.NextValue(); // prime
            var free = new PerformanceCounter("GPU Adapter Memory", "Dedicated Memory", instance, true);
            free.NextValue(); // prime
            long capacity = (long)used.NextValue() + (long)free.NextValue();
            used.Dispose();
            free.Dispose();
            if (capacity > 0)
            {
                return capacity;
            }
        }
        catch
        {
            // fall through to the committed-based fallback below
        }

        // Some driver builds omit the free counter; fall back to committed as a proxy.
        try
        {
            var committed = new PerformanceCounter("GPU Adapter Memory", "Total Committed", instance, true);
            committed.NextValue(); // prime
            long v = (long)committed.NextValue();
            committed.Dispose();
            return v > 0 ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Counts physical GPUs via WMI: those on the PCI bus (PNPDeviceID starts with "PCI\")
    /// that report a video memory size (AdapterRAM). Virtual display adapters live under
    /// ROOT\DISPLAY and report no memory, so they are excluded.
    /// </summary>
    private static int CountPhysicalGpusFromWmi()
    {
        var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT PNPDeviceID, AdapterRAM FROM Win32_VideoController");

        int count = 0;
        foreach (System.Management.ManagementObject obj in searcher.Get())
        {
            string pnpId = obj["PNPDeviceID"]?.ToString()?.ToUpper() ?? "";
            if (!pnpId.StartsWith("PCI\\"))
            {
                continue;
            }

            if (HasMemory(obj["AdapterRAM"]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True if the WMI AdapterRAM value indicates a real, non-empty video memory size.
    /// </summary>
    private static bool HasMemory(object? value)
    {
        try
        {
            return value != null && value.ToString() != "" && value.ToString() != "0";
        }
        catch
        {
            return false;
        }
    }
}
