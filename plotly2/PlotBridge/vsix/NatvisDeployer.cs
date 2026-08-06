using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// Copies the bundled PlotBridge.natvis into the user's Visualizers folder,
    /// which is where the debugger looks for it. Deploying via the VSIX itself
    /// would hide the file inside the extension folder; here it lands somewhere
    /// the user can open and add types to, which is the whole point of natvis-based
    /// registration.
    /// </summary>
    internal static class NatvisDeployer
    {
        private const string FileName = "PlotBridge.natvis";

        public static string TargetFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Visual Studio 2022", "Visualizers");

        public static string TargetPath => Path.Combine(TargetFolder, FileName);

        /// <returns>A human-readable note about what happened, or null if the file
        /// was already identical.</returns>
        public static string Deploy()
        {
            try
            {
                var bundled = ReadBundled();
                if (bundled == null) return null;

                Directory.CreateDirectory(TargetFolder);

                if (!File.Exists(TargetPath))
                {
                    File.WriteAllText(TargetPath, bundled, new UTF8Encoding(false));
                    return "Installed " + TargetPath + " — start or restart a debug session to pick it up.";
                }

                var existing = File.ReadAllText(TargetPath);
                if (Normalize(existing) == Normalize(bundled)) return null;

                // Never clobber the user's edits: that file is the extension point.
                var sidecar = TargetPath + ".new";
                File.WriteAllText(sidecar, bundled, new UTF8Encoding(false));
                return "Your " + FileName + " differs from this version's; the new copy was written " +
                       "alongside it as " + Path.GetFileName(sidecar) + " and your file was left alone.";
            }
            catch (Exception ex)
            {
                return "Could not install " + FileName + ": " + ex.Message;
            }
        }

        private static string ReadBundled()
        {
            var asm = Assembly.GetExecutingAssembly();

            // Shipped as an embedded resource so there is exactly one copy to keep
            // in step with PlotBridgeGuids.Service.
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith(FileName, StringComparison.OrdinalIgnoreCase)) continue;
                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) continue;
                    using (var reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }

            // Fall back to a copy sitting next to the assembly.
            var local = Path.Combine(Path.GetDirectoryName(asm.Location) ?? ".", FileName);
            return File.Exists(local) ? File.ReadAllText(local) : null;
        }

        private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
    }
}
