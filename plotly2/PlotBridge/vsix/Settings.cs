using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// A handful of remembered choices, in a flat key=value file next to the
    /// server's own data. Deliberately not VS settings storage: this needs to be
    /// readable and hand-editable, and it is four strings.
    /// </summary>
    internal static class Settings
    {
        public static string Board = "default";
        public static string Chart = "main";
        public static string Mode = "auto";
        public static bool Replace = true;
        public static bool AskEveryTime = true;
        public static int MaxPoints = 200000;
        public static int Port = 8777;

        private static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlotBridge", "vsix.settings");

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                foreach (var raw in File.ReadAllLines(Path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "board": Board = value; break;
                        case "chart": Chart = value; break;
                        case "mode": Mode = value; break;
                        case "replace": Replace = ParseBool(value, true); break;
                        case "askEveryTime": AskEveryTime = ParseBool(value, true); break;
                        case "maxPoints": MaxPoints = ParseInt(value, 200000); break;
                        case "port": Port = ParseInt(value, 8777); break;
                    }
                }
                PlotBridgeClient.Port = Port;
            }
            catch
            {
                // A malformed settings file must never stop the extension loading.
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                var lines = new List<string>
                {
                    "# PlotBridge extension settings. Delete a line to restore its default.",
                    "board=" + Board,
                    "chart=" + Chart,
                    "mode=" + Mode,
                    "replace=" + (Replace ? "true" : "false"),
                    "askEveryTime=" + (AskEveryTime ? "true" : "false"),
                    "maxPoints=" + MaxPoints.ToString(CultureInfo.InvariantCulture),
                    "port=" + Port.ToString(CultureInfo.InvariantCulture),
                };
                File.WriteAllLines(Path, lines);
            }
            catch
            {
            }
        }

        private static bool ParseBool(string s, bool fallback) => bool.TryParse(s, out var b) ? b : fallback;

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0 ? i : fallback;
    }
}
