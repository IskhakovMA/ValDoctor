using System.IO;
using System.Windows;
using RiotFix.App.Services;
using RiotFix.App.ViewModels;
using RiotFix.Core;
using RiotFix.Core.Diagnostics;
using RiotFix.Core.Elevation;
using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;

namespace RiotFix.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 1. Elevated helper: one hosts operation, no UI, then exit. Validates everything it receives.
        //    It deliberately writes no log files: an elevated process must not write into user-controlled folders.
        if (ElevatedProtocol.IsElevatedInvocation(args))
        {
            var hosts = new HostsFileService(new PhysicalFileSystem(), HostsLocator.GetHostsPath(), NullLog.Instance);
            return (int)ElevatedRunner.Run(args, hosts, DnsCache.Flush, NullLog.Instance);
        }

        var log = new FileLog(AppInfo.LogDirectory);

        // 2. Read-only network self-check used by CI on a real Windows runner. Never modifies hosts.
        if (args is ["--self-check", var reportPath])
        {
            return SelfCheck(reportPath, log);
        }

        var app = new App();
        app.InitializeComponent();
        ThemeManager.Initialize(app);

        // 3. GUI smoke test used by CI: renders the main states to PNG. Never modifies hosts.
        if (args is ["--smoke-test", var outputDir])
        {
            return SmokeTest.Run(app, outputDir, log);
        }

        app.DispatcherUnhandledException += (_, e) =>
        {
            log.Error("Unhandled UI exception.", e.Exception);
            MessageBox.Show("Произошла непредвиденная ошибка. Подробности записаны в журнал RiotFix.", "RiotFix",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        log.Info($"RiotFix {AppInfo.Version} started (elevated: {ElevationService.IsAdministrator()}).");
        var window = new MainWindow();
        window.DataContext = new MainViewModel(AppComposition.Create(log), new MessageBoxDialogs(window));
        return app.Run(window);
    }

    private static int SelfCheck(string reportPath, FileLog log)
    {
        try
        {
            var engine = AppComposition.Create(log).CreateEngine();
            var report = Task.Run(() => engine.RunAsync(RiotServices.All, null, CancellationToken.None)).GetAwaiter().GetResult();
            var status = Texts.ForReport(report);
            File.WriteAllText(reportPath, $"{status.Headline}\n{status.Body}\n\n{ReportFormatter.Format(report, AppInfo.Version)}");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Self-check failed.", ex);
            File.WriteAllText(reportPath, "SELF-CHECK FAILED: " + ex);
            return 1;
        }
    }
}
