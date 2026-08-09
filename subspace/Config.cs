using System.Text.Json;

namespace Subspace;

public class Config
{
    public string VhdxPath { get; set; } = "";
    public string Pin { get; set; } = "";

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".subspace");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public static Config Load()
    {
        if (!File.Exists(ConfigFile))
        {
            throw new FileNotFoundException($"Config not found: {ConfigFile}");
        }
        var json = File.ReadAllText(ConfigFile);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Config>(json, opts) ?? new Config();
    }
}
