using System;
using System.Collections.Generic;
using System.IO;

class Program
{
    // Dictionary of forced values (key = setting name, value = new value)
    static readonly Dictionary<string, string> forcedValues = new Dictionary<string, string>
    {
        { "limit", "768" },
        { "streaming_buffer_size", "128" },
        { "streaming_texture_pool_size", "1024" },
        { "streaming_max_open_streams", "64" },
        { "max_age_out_tiles_per_frame ", "16" },
        { "max_streaming_tiles_per_frame ", "16" },
        { "tile_staging_buffer_size", "256" }
        // Add more keys here
    };

    static void Main(string[] args)
    {
        string folderPath = @"S:\dist\steamapps\common\Warhammer 40,000 DARKTIDE\bundle\application_settings"; // change this to your folder
        string[] iniFiles = Directory.GetFiles(folderPath, "*.ini");

        foreach (var file in iniFiles)
        {
            Console.WriteLine($"Processing {file}...");

            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var kv in forcedValues)
                {
                    if (lines[i].TrimStart().StartsWith(kv.Key + "=", StringComparison.OrdinalIgnoreCase) ||
                        lines[i].TrimStart().StartsWith(kv.Key + " =", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{kv.Key} = {kv.Value}";
                        Console.WriteLine($"Set {kv.Key} to {kv.Value} in {Path.GetFileName(file)}");
                        break;
                    }
                }
            }

            File.WriteAllLines(file, lines);
        }

        Console.WriteLine("Done!");
    }
}
