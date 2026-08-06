using System;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// The magnifying-glass action. The debugger resolves the natvis ServiceId to
    /// this object and hands over the variable's property, already scoped to the
    /// right stack frame and process.
    /// </summary>
    [ComVisible(true)]
    public sealed class VisualizerService : SPlotBridgeVisualizer, IVsCppDebugUIVisualizer
    {
        public int DisplayValue(uint ownerHwnd, uint visualizerId, IDebugProperty3 pDebugProperty)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                Plot(ownerHwnd, pDebugProperty);
            }
            catch (Exception ex)
            {
                Report("PlotBridge hit an unexpected error:\n\n" + ex, OLEMSGICON.OLEMSGICON_CRITICAL);
            }

            // Always S_OK: a failure HRESULT makes the debugger show its own opaque
            // error on top of whatever we already explained.
            return VSConstants.S_OK;
        }

        private static void Plot(uint ownerHwnd, IDebugProperty3 property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (property == null)
            {
                Report("The debugger did not supply a value to plot.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            SetStatus("PlotBridge: reading points…");
            var result = DebuggerPoints.Extract(property, Settings.MaxPoints, out var error);

            if (result == null)
            {
                SetStatus(string.Empty);
                Report(error ?? "Could not read any points from this variable.", OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var series = result.Name;
            var chart = Settings.Chart;
            var board = Settings.Board;
            var mode = Settings.Mode;
            var replace = Settings.Replace;

            if (Settings.AskEveryTime)
            {
                SetStatus(string.Empty);
                var dialog = new PushDialog((IntPtr)ownerHwnd, series, result.TypeName, result.Count, result.ElapsedMs);
                if (dialog.ShowDialog() != true) return;

                series = string.IsNullOrEmpty(dialog.Series) ? series : dialog.Series;
                chart = string.IsNullOrEmpty(dialog.Chart) ? chart : dialog.Chart;
                board = string.IsNullOrEmpty(dialog.Board) ? board : dialog.Board;
                mode = dialog.Mode;
                replace = dialog.Replace;

                Settings.Chart = chart;
                Settings.Board = board;
                Settings.Mode = mode;
                Settings.Replace = replace;
                if (dialog.DontAskAgain) Settings.AskEveryTime = false;
                Settings.Save();
            }

            if (!PlotBridgeClient.PushText(board, chart, series, mode, replace, result.Text, out var message))
            {
                SetStatus(string.Empty);
                Report("PlotBridge could not push the points.\n\n" + message, OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            // Naming the route is worth the characters: which of the extraction
            // strategies fired is the first thing you want to know when a plot
            // looks wrong or arrives slowly.
            var via = string.IsNullOrEmpty(result.Via) ? "" : $" via {result.Via}";
            SetStatus($"PlotBridge: {result.Count:N0} points{via} → {board}/{chart}/{series} in {result.ElapsedMs} ms");

            if (result.Warning != null)
            {
                Report(result.Warning + "\n\nRaise the limit with maxPoints in:\n" +
                       System.IO.Path.Combine(
                           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "PlotBridge", "vsix.settings"),
                       OLEMSGICON.OLEMSGICON_INFO);
            }
        }

        private static void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)
                {
                    bar.FreezeOutput(0);
                    bar.SetText(text);
                }
            }
            catch
            {
            }
        }

        private static void Report(string message, OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                VsShellUtilities.ShowMessageBox(
                    ServiceProvider.GlobalProvider,
                    message,
                    "PlotBridge",
                    icon,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch
            {
                MessageBox.Show(message, "PlotBridge");
            }
        }
    }
}
