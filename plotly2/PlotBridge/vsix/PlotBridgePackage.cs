using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace PlotBridge.Vsix
{
    public static class PlotBridgeGuids
    {
        /// <summary>Quoted verbatim in PlotBridge.natvis as the UIVisualizer
        /// ServiceId. The debugger issues a QueryService for it, and that is what
        /// loads this package — the manifest declares no auto-load.</summary>
        public const string Service = "3f7a1c58-9e2b-4d16-8a54-c6b0f39d7e42";

        public const string Package = "b4d92e07-5c31-4f8a-9b76-2e1a8d4c5f63";
    }

    /// <summary>Service type the natvis ServiceId resolves to.</summary>
    [Guid(PlotBridgeGuids.Service)]
    public interface SPlotBridgeVisualizer
    {
    }

    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PlotBridgeGuids.Package)]
    [ProvideService(typeof(SPlotBridgeVisualizer), IsAsyncQueryable = false)]
    // Auto-load is here to break a bootstrap deadlock, not for convenience.
    // Deploying the natvis is what makes the debugger query our ServiceId, and
    // that query is what would otherwise load this package - so on a fresh
    // install nothing ever ran and the visualizer never appeared. Loading when a
    // solution opens gets the file in place before the first debug session reads
    // the Visualizers folder. Background load, and the work is one file compare.
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class PlotBridgePackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Settings.Load();

            // The debugger queries this service synchronously on the UI thread, so
            // the object has to exist before initialisation returns.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            AddService(typeof(SPlotBridgeVisualizer), (_, __, ___) => Task.FromResult<object>(new VisualizerService()), promote: true);

            // Runs on solution open, so the file is in place before the first debug
            // session reads the Visualizers folder. This is the bootstrap step: the
            // debugger cannot ask for our service until this file exists.
            var note = NatvisDeployer.Deploy();
            if (!string.IsNullOrEmpty(note)) WriteToOutput(note);
        }

        private void WriteToOutput(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (GetService(typeof(SVsGeneralOutputWindowPane)) is IVsOutputWindowPane pane)
                {
                    pane.OutputStringThreadSafe("PlotBridge: " + text + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
