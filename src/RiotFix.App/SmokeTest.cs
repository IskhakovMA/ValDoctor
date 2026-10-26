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

            ThemeManager.Apply(app, dark: false);
            Render(window, outputDir, "01-light-initial.png");

            var fix = SampleData.FixAvailable();
            vm.ShowReport(fix, Texts.ForReport(fix));
            Render(window, outputDir, "02-light-fix-available.png");

            var expander = FindChild<Expander>(window);
            if (expander is not null)
            {
                window.Height = 1000;
                expander.IsExpanded = true;
                Render(window, outputDir, "03-light-details.png");
                expander.IsExpanded = false;
                window.Height = 660;
            }

            ThemeManager.Apply(app, dark: true);
            var healthy = SampleData.AllHealthy();
            vm.ShowReport(healthy, Texts.ForReport(healthy));
            Render(window, outputDir, "04-dark-healthy.png");

            vm.ShowReport(fix, Texts.ForReport(fix));
            Render(window, outputDir, "05-dark-fix-available.png");

            window.Width = 460;
            window.Height = 520;
            Render(window, outputDir, "06-dark-min-size.png");

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

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private sealed class NoDialogs : IDialogs
    {
        public bool Confirm(string title, string message) => false;

        public void Info(string title, string message)
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

    private static DiagnosisReport Report(List<DomainDiagnosis> domains, Verdict verdict) =>
        new(DateTimeOffset.Now, TimeSpan.FromSeconds(3.2), domains, false, true, false, [],
            FixPlanner.Create(domains, HostsDocument.Empty()), verdict);

    private static DomainDiagnosis Healthy(RiotService s, string ip) =>
        new(s, new SystemDnsAnswer([IPAddress.Parse(ip)], DnsFailureKind.None), [Ok(ip, 403)], DomainHealth.Healthy, HostsOverride.None, [], []);

    private static DomainDiagnosis Broken(RiotService s, string ip, ProbeFailure failure, string? candidate)
    {
        var probe = new ProbeResult(IPAddress.Parse(ip), failure == ProbeFailure.TlsReset ? ProbeStage.TcpConnected : ProbeStage.None, failure, TimeSpan.FromSeconds(5));
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
