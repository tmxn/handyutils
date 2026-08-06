using System.Text.Json.Serialization;

namespace PlotBridge.Server;

/// <summary>How a series is drawn. Colour is stored as a palette *slot* so the
/// page can re-resolve it per theme; an explicit hex in <see cref="Color"/>
/// is a user override and wins in both themes.</summary>
public sealed class Style
{
    public string Mode { get; set; } = "lines+markers";   // markers | lines | lines+markers
    public double Size { get; set; } = 3;
    public int? Slot { get; set; }
    public string? Color { get; set; }
}

public sealed class Series
{
    public string Name { get; set; } = "";
    public double[] X { get; set; } = [];
    public double[] Y { get; set; } = [];
    public double[]? Z { get; set; }
    public Style Style { get; set; } = new();
    public bool Visible { get; set; } = true;
    public long UpdatedMs { get; set; }
    public Dictionary<string, string>? Meta { get; set; }

    [JsonIgnore] public int Count => Y.Length;
}

public sealed class Chart
{
    public string Name { get; set; } = "main";
    public string Mode { get; set; } = "auto";       // auto | 2d | 3d
    public bool Uniform { get; set; } = true;
    public List<Series> Series { get; set; } = [];
}

public sealed class Board
{
    public string Name { get; set; } = "default";
    public List<Chart> Charts { get; set; } = [];
}

/// <summary>Body of POST /push. Every coordinate field is optional; the first
/// one present wins, in the order X/Y/Z, Points, Values, Text.</summary>
public sealed class PushRequest
{
    public string? Board { get; set; }
    public string? Chart { get; set; }
    public string? Series { get; set; }
    public string? Mode { get; set; }

    public double[]? X { get; set; }
    public double[]? Y { get; set; }
    public double[]? Z { get; set; }
    public double[][]? Points { get; set; }
    public double[]? Values { get; set; }
    public string? Text { get; set; }

    public Style? Style { get; set; }
    public bool? Replace { get; set; }
    public bool? ClearChart { get; set; }
    public Dictionary<string, string>? Meta { get; set; }
}
