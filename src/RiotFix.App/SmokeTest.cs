using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RiotFix.App.Services;
using RiotFix.App.ViewModels;
using RiotFix.Core;
using RiotFix.Core.Diagnostics;
using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;
using RiotFix.Core.Network;
using RiotFix.Core.Updates;

namespace RiotFix.App;

/// <summary>
/// Renders the real main window in several states (light/dark, 150 % scale) to PNG files.
/// Uses sample data only: no network access, no elevation, the hosts file is only read.
/// </summary>
internal static class SmokeTest
{
    private const double Scale = 1.5;

    public static int Run(App app, string outputDir, IRiotFixLog log)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var window = new MainWindow
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                ShowInTaskbar = false,
            };
            var vm = new MainViewModel(AppComposition.Create(log), new NoDialogs());
            window.DataContext = vm;
            window.Show();

            void State(string file, DiagnosisReport? report = null, StatusMessage? message = null, bool canRevert = false)
            {
                if (report is not null)
                {
                    vm.ShowReport(report, message ?? Texts.ForReport(report));
                }

                // After ShowReport (which re-reads the real, unchanged hosts file of the runner).
                vm.Preview(canRevert: canRevert);

                Render(window, outputDir, file);
                AssertPlainMainScreen(window, file);
            }

            ThemeManager.Apply(app, dark: false);
            State("01-light-initial.png");
            vm.Preview(busy: new StatusMessage(StatusTone.Neutral, "Проверяем подключение…", "Это займёт несколько секунд."));
            Render(window, outputDir, "02-light-checking.png");
            AssertPlainMainScreen(window, "02-light-checking.png");
            vm.Preview();
            State("03-light-working.png", SampleData.AllHealthy());
            State("04-light-problem-fixable.png", SampleData.FixAvailable());

            var after = SampleData.AllHealthy();
            State("05-light-applied.png", after, Texts.AfterFix(after, SampleData.FixAvailable().Plan.NewPins), canRevert: true);
            State("06-light-no-safe-fix.png", SampleData.NoFix());

            // The technical report is still there, one click away.
            var details = new DetailsWindow { DataContext = vm, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            details.Show();
            Render(details, outputDir, "07-light-details-window.png");
            if (!vm.Details.Contains("auth.riotgames.com", StringComparison.Ordinal) || PlainTextPolicy.FindTechnicalTerms(vm.Details).Count == 0)
            {
                throw new InvalidOperationException("The details window must keep the full technical report.");
            }

            details.Close();

            ThemeManager.Apply(app, dark: true);
            State("08-dark-working-limited.png", SampleData.Limited());
            vm.ShowUpdate(new ReleaseInfo(SemVersion.Create(9, 9, 9), "v9.9.9", "RiotFix v9.9.9", "Пример уведомления об обновлении (smoke test)",
                UpdateChecker.ReleaseUrl("v9.9.9"), true));
            State("09-dark-problem-fixable-update.png", SampleData.FixAvailable());
            vm.ShowUpdate(null);

            window.Width = 420;
            window.Height = 440;
            State("10-dark-min-size-revert.png", SampleData.NoFix(), canRevert: true);

            File.WriteAllText(Path.Join(outputDir, "smoke-ok.txt"), $"RiotFix {AppInfo.Version} smoke test passed");
            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Join(outputDir, "smoke-error.txt"), ex.ToString());
            log.Error("Smoke test failed.", ex);
            return 1;
        }
    }

    /// <summary>Fails if any visible text on the main window contains technical details (IPs, hosts, protocols, paths).</summary>
    private static void AssertPlainMainScreen(Window window, string state)
    {
        var texts = new List<string>();
        Collect(window, texts);
        var found = texts.SelectMany(PlainTextPolicy.FindTechnicalTerms).Distinct().ToList();
        if (found.Count > 0)
        {
            throw new InvalidOperationException($"{state}: technical details on the main screen: {string.Join(", ", found)}");
        }
    }

    private static void Collect(DependencyObject node, List<string> texts)
    {
        if (node is UIElement { IsVisible: false })
        {
            return;
        }

        if (node is TextBlock tb)
        {
            texts.Add(tb.Text);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            Collect(VisualTreeHelper.GetChild(node, i), texts);
        }
    }

    private static void Render(Window window, string dir, string name)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * Scale),
            (int)Math.Ceiling(root.ActualHeight * Scale),
            96 * Scale,
            96 * Scale,
            PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Join(dir, name));
        encoder.Save(stream);
    }

    private sealed class NoDialogs : IDialogs
    {
        public bool Confirm(string title, string message) => false;

        public void Info(string title, string message)
        {
        }

        public void ShowDetails(object dataContext)
        {
        }
    }
}

/// <summary>Representative diagnosis results for the smoke test screenshots.</summary>
internal static class SampleData
{
    public static DiagnosisReport AllHealthy()
    {
        var domains = RiotServices.All.Select((s, i) => Healthy(s, $"104.16.{100 + i}.50")).ToList();
        return Report(domains, Verdict.AllHealthy);
    }

    public static DiagnosisReport FixAvailable()
    {
        var all = RiotServices.All;
        var domains = new List<DomainDiagnosis>
        {
            Broken(all[0], "104.16.119.50", ProbeFailure.TlsReset, candidate: "104.16.120.50"),
            Healthy(all[1], "104.16.56.40"),
            Broken(all[2], "104.18.156.37", ProbeFailure.TcpTimeout, candidate: "104.18.157.37"),
            Healthy(all[3], "104.16.56.21"),
            Healthy(all[4], "104.16.55.40"),
            Broken(all[5], "172.64.151.57", ProbeFailure.TcpTimeout, candidate: null),
        };
        return Report(domains, Verdict.FixAvailable);
    }

    /// <summary>The state seen on the real affected PC: essential services fine, account/data TLS timeout, no fix.</summary>
    public static DiagnosisReport Limited()
    {
        var all = RiotServices.All;
        var domains = new List<DomainDiagnosis>
        {
            Healthy(all[0], "104.16.120.50"),
            Broken(all[1], "104.16.56.40", ProbeFailure.TlsTimeout, candidate: null),
            Healthy(all[2], "104.18.157.37"),
            Healthy(all[3], "104.16.56.21"),
            Broken(all[4], "104.16.56.40", ProbeFailure.TlsTimeout, candidate: null),
            Healthy(all[5], "104.18.36.199"),
        };
        return Report(domains, Verdict.LimitedConnectivity);
    }

    /// <summary>An essential service is down and no verified alternative exists.</summary>
    public static DiagnosisReport NoFix()
    {
        var all = RiotServices.All;
        var domains = all.Select((s, i) => i == 0
            ? Broken(s, "104.16.119.50", ProbeFailure.TlsTimeout, candidate: null)
            : Healthy(s, $"104.16.{100 + i}.50")).ToList();
        return Report(domains, Verdict.ProblemsWithoutFix);
    }

    private static DiagnosisReport Report(List<DomainDiagnosis> domains, Verdict verdict) =>
        new(DateTimeOffset.Now, TimeSpan.FromSeconds(3.2), domains, false, true, false, [],
            FixPlanner.Create(domains, HostsDocument.Empty()), verdict);

    private static DomainDiagnosis Healthy(RiotService s, string ip) =>
        new(s, new SystemDnsAnswer([IPAddress.Parse(ip)], DnsFailureKind.None), [Ok(ip, 403)], DomainHealth.Healthy, HostsOverride.None, [], []);

    private static DomainDiagnosis Broken(RiotService s, string ip, ProbeFailure failure, string? candidate)
    {
        var probe = new ProbeResult(IPAddress.Parse(ip), failure is ProbeFailure.TlsReset or ProbeFailure.TlsTimeout ? ProbeStage.TcpConnected : ProbeStage.None, failure, TimeSpan.FromSeconds(5));
        var trustedIps = candidate is null ? [ip] : new[] { ip, candidate };
        var trusted = new List<TrustedDnsAnswer>
        {
            new("Cloudflare DNS", true, trustedIps.Select(IPAddress.Parse).ToList()),
            new("Google Public DNS", true, trustedIps.Select(IPAddress.Parse).ToList()),
        };
        var candidates = candidate is null
            ? new List<CandidateEvidence>()
            : [new CandidateEvidence(IPAddress.Parse(candidate), ["Cloudflare DNS", "Google Public DNS"], [Ok(candidate, 403), Ok(candidate, 403)])];
        return new DomainDiagnosis(s, new SystemDnsAnswer([IPAddress.Parse(ip)], DnsFailureKind.None), [probe], DomainHealth.Failed, HostsOverride.None, trusted, candidates);
    }

    private static ProbeResult Ok(string ip, int status) =>
        new(IPAddress.Parse(ip), ProbeStage.HttpResponded, ProbeFailure.None, TimeSpan.FromMilliseconds(180), status,
            CertificateSubject: "CN=riotgames.com", CertificateIssuer: "CN=WE1, O=Google Trust Services, C=US", TlsProtocol: "Tls13");
}
