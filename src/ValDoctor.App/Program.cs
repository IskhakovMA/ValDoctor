using System.IO;
using System.Windows;
using ValDoctor.App.Services;
using ValDoctor.App.ViewModels;
using ValDoctor.Core;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Elevation;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Logging;

namespace ValDoctor.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 1. Elevated helper: one hosts operation, no UI, then exit. Validates everything it receives.
        //    It deliberately writes no log files: an elevated process must not write into user-controlled folders.
        if (ElevatedProtocol.IsElevatedInvocation(args))
        {
            NativeHardening.RestrictDllSearchToSystem32();
            var hosts = new HostsFileService(new PhysicalFileSystem(), HostsLocator.GetHostsPath(), NullLog.Instance);
            return (int)ElevatedRunner.Run(args, hosts, DnsCache.Flush, NullLog.Instance);
        }

        // Running elevated (e.g. "Run as administrator"): write nothing into the user-writable profile, where a
        // same-user program could redirect elevated file writes/deletes with junctions or symlinks.
        IAppLog log = ElevationService.IsAdministrator() ? NullLog.Instance : new FileLog(AppInfo.LogDirectory);

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
            MessageBox.Show("Произошла непредвиденная ошибка. Подробности записаны в журнал ValDoctor.", "ValDoctor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        log.Info($"ValDoctor {AppInfo.Version} started (elevated: {ElevationService.IsAdministrator()}).");
        var window = new MainWindow();
        var composition = AppComposition.Create(log);
        var viewModel = new MainViewModel(composition, new MessageBoxDialogs(window));
        window.DataContext = viewModel;
        window.ContentRendered += (_, _) => viewModel.StartUpdateCheck(composition.CreateUpdateChecker());
        return app.Run(window);
    }

    private static int SelfCheck(string reportPath, IAppLog log)
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
