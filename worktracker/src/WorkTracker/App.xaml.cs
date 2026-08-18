using System.Windows;
using System.Windows.Threading;
using WorkTracker.Services;

namespace WorkTracker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort logging so a real-repo test can be diagnosed even if the UI throws.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("unhandled dispatcher exception", args.Exception);
            // Leave Handled = false: keep default WPF behavior (crash dialog).
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("unhandled AppDomain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppLog.Info($"=== WorkTracker startup (pid {Environment.ProcessId}, " +
                    $"{Environment.Version}, process {Environment.ProcessPath}) ===");

        ConfigStore.BootstrapDataDir();
        var config = new ConfigStore().Load();
        AppLog.Info($"config: repo='{config.RepoPath}', developers={config.Developers.Count}, " +
                    $"llm={config.Llm.Backend} " +
                    (config.Llm.Backend == "pi"
                        ? $"{config.Llm.Command} {string.Join(' ', config.Llm.Args)} "
                        : $"{config.Llm.LlamaEndpoint} model={config.Llm.LlamaModel} ") +
                    $"timeout={config.Llm.TimeoutSeconds}s, thinking={config.Llm.ThinkingEffort}, " +
                    $"theme={config.Theme}");

        Theme.Initialize(config.Theme);

        var win = new MainWindow(config);
        MainWindow = win;
        win.Show();
    }
}
