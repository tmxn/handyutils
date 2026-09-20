using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GpuVramMonitor;

/// <summary>
/// A process's dedicated (VRAM) allocation on the discrete GPU.
/// </summary>
public readonly struct GpuProcessUsage
{
    public int Pid { get; init; }
    public string ProcessName { get; init; }
    public double DedicatedMb { get; init; }
}

/// <summary>
/// Lists the processes that currently hold dedicated GPU memory.
///
/// Data source: the "GPU Process Memory" performance counter category, one
/// "Dedicated Usage" sample per (pid, adapter) pair, with instance names like
/// "pid_1234_luid_0x00000000_0x834815e3_phys_0". We keep only instances whose adapter
/// LUID is one of the physical (dedicated-VRAM) adapters reported by
/// <see cref="GpuVramReader.GetPhysicalGpuLuids"/> — no hardcoded LUID.
///
/// We deliberately do NOT pin the list to a single "discrete" GPU: which physical
/// adapter is the discrete one cannot be told from the perf counters alone in an
/// idle-proof way (an idle discrete GPU's committed/usage counters read low and a busy
/// iGPU can outrank it, which made the list come back empty). Only the discrete GPU has
/// dedicated VRAM, so any process reporting dedicated usage is, by definition, on it —
/// iGPU/virtual adapters report zero dedicated usage and drop out naturally.
/// </summary>
public static class GpuProcessReader
{
    // Instance counters are PDH queries that must be primed once and then stay open,
    // so we cache them per instance name and drop the ones whose process exited.
    private static readonly Dictionary<string, PerformanceCounter> _counters = [];
    private static readonly object _lock = new();

    private static readonly Regex InstanceRegex = new(
        @"^pid_(\d+)_luid_(0x[0-9a-f]+)_(0x[0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True if the instance name carries one of the given adapters' LUIDs, regardless of
    /// whether the two halves (low, high) are emitted in low|high or high|low order.
    /// Different Windows perf-counter categories format the same LUID in different
    /// orders, so we match both.
    /// </summary>
    private static bool IsPhysicalAdapter(string instance, List<(string Low, string High)> luids)
    {
        foreach (var (low, high) in luids)
        {
            if (instance.Contains($"luid_{low}_{high}", StringComparison.OrdinalIgnoreCase)
                || instance.Contains($"luid_{high}_{low}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns per-process dedicated VRAM on the physical (dedicated-VRAM) GPU, largest
    /// first. Processes that have exited since the counter sample are reported as "pid N".
    /// </summary>
    public static List<GpuProcessUsage> GetDiscreteGpuProcesses()
    {
        var physicalLuids = GpuVramReader.GetPhysicalGpuLuids()
            .Select(x => (x.Low, x.High))
            .ToList();
        if (physicalLuids.Count == 0)
        {
            return [];
        }

        // Snapshot the instance names that belong to a physical adapter right now.
        List<string> liveInstances;
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            liveInstances = category.GetInstanceNames()
                .Where(n => IsPhysicalAdapter(n, physicalLuids))
                .ToList();
        }
        catch
        {
            return [];
        }

        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            names[p.Id] = p.ProcessName;
            p.Dispose();
        }

        var result = new List<GpuProcessUsage>();

        lock (_lock)
        {
            // Drop cached counters whose instance is gone (process exited).
            foreach (var stale in _counters.Keys.Except(liveInstances).ToList())
            {
                _counters[stale].Dispose();
                _counters.Remove(stale);
            }

            foreach (var instance in liveInstances)
            {
                if (!_counters.TryGetValue(instance, out var counter))
                {
                    try
                    {
                        counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, true);
                        counter.NextSample(); // prime the PDH query
                        _counters[instance] = counter;
                    }
                    catch
                    {
                        continue; // instance may have vanished between snapshot and open
                    }
                }

                double mb;
                try
                {
                    mb = counter.NextValue() / (1024.0 * 1024.0);
                }
                catch
                {
                    continue;
                }

                if (mb <= 0)
                {
                    continue;
                }

                var m = InstanceRegex.Match(instance);
                int pid = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                result.Add(new GpuProcessUsage
                {
                    Pid = pid,
                    ProcessName = names.TryGetValue(pid, out var name) ? name : $"pid {pid}",
                    DedicatedMb = Math.Round(mb, 1)
                });
            }
        }

        return result.OrderByDescending(r => r.DedicatedMb).ToList();
    }
}
