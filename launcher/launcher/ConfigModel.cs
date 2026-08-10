using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Launcher;

public class AppConfig
{
    public List<CategoryData> Categories { get; set; } = new();
}

public class CategoryData
{
    public string Name { get; set; } = string.Empty;
    public List<LauncherItem> Items { get; set; } = new();
}

public class LauncherItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Args { get; set; } = string.Empty;
}

// SOURCE GENERATOR CONTEXT (Mandatory for Native AOT)
[JsonSerializable(typeof(AppConfig))]
public partial class AppConfigContext : JsonSerializerContext { }
