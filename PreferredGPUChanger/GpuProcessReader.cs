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
/// Lists the processes that currently hold dedicated GPU memory on the discrete GPU.
///
/// Data source: the "GPU Process Memory" performance counter category, one
/// "Dedicated Usage" sample per (pid, adapter) pair, with instance names like
/// "pid_1234_luid_0x00000000_0x834815e3_phys_0". We keep only the instances whose
/// LUID matches the discrete GPU identified by <see cref="GpuVramReader.GetDiscreteGpuLuid"/>
/// (largest-VRAM physical adapter, same heuristic as the VRAM label) — no hardcoded LUID.
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
    /// Returns per-process dedicated VRAM on the discrete GPU, largest first.
    /// Processes that have exited since the counter sample are reported as "pid N".
    /// </summary>
    public static List<GpuProcessUsage> GetDiscreteGpuProcesses()
    {
        var luid = GpuVramReader.GetDiscreteGpuLuid();
        if (luid == null)
        {
            return [];
        }

        string luidTag = $"luid_{luid.Value.Low}_{luid.Value.High}";

        // Snapshot the instance names that belong to the discrete GPU right now.
        List<string> liveInstances;
        try
        {
            var category = new PerformanceCounterCategory("GPU Process Memory");
            liveInstances = category.GetInstanceNames()
                .Where(n => n.Contains(luidTag, StringComparison.OrdinalIgnoreCase))
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
