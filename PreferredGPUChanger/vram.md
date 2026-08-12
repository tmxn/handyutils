The easiest and most reliable way in C# is using Windows Performance Counters via the System.Diagnostics.PerformanceCounter class. This mirrors the underlying logic used by Task Manager and the PowerShell scripts.

Step 1: Add the NuGet Package
If you are using .NET 6, 7, 8, or 9, install the package via CLI or Package Manager:

Bash
dotnet add package System.Diagnostics.PerformanceCounter

Code:
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

class Program
{
    static void Main()
    {
        var category = new PerformanceCounterCategory("GPU Adapter Memory");
        string[] instanceNames = category.GetInstanceNames();

        var counters = new List<(string Instance, PerformanceCounter Counter)>();

        foreach (var name in instanceNames)
        {
            // Only capture total dedicated usage instances (phys_0)
            if (name.EndsWith("phys_0"))
            {
                var pc = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", name);
                pc.NextValue(); // First read initializes counter
                counters.Add((name, pc));
            }
        }

        Thread.Sleep(1000);

        while (true)
        {
            Console.Clear();
            Console.WriteLine("=== Per-GPU Dedicated VRAM Monitor ===");

            foreach (var item in counters)
            {
                float bytes = item.Counter.NextValue();
                double mb = bytes / (1024 * 1024);
                double gb = mb / 1024;

                Console.WriteLine($"GPU Instance : {item.Instance}");
                Console.WriteLine($"Dedicated VRAM: {mb:F2} MB (~{gb:F2} GB)");
                Console.WriteLine("----------------------------------------");
            }

            Thread.Sleep(1000);
        }
    }
}
