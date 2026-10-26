using System.Net;
using System.Text;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Network;
using ValDoctor.Core.Tests.Fakes;
using ValDoctor.Core.Updates;

namespace ValDoctor.Core.Tests;

/// <summary>Regression tests for the findings of the v0.3.0 security audit (SECURITY_AUDIT.md).</summary>
public class SecurityAuditRegressionTests
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    private const string Auth = "auth.riotgames.com";
    private static readonly HostPin AuthPin = new(Auth, IPAddress.Parse("104.16.120.50"));
    private static readonly byte[] Original = Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n10.1.1.1 nas.local\r\n");

    // ---- M1: a hostile system DNS must not get its own address pinned --------------------------------------------

    private static async Task<DiagnosisReport> RunWithHostileSystemDns(FakeTrustedResolver doh)
    {
        // System DNS answers [relay, dead]; the relay forwards to the real origin, so TLS validates.
        var dns = new FakeSystemResolver().Set(Auth, "45.10.20.30", "104.16.119.50");
        var prober = new FakeProber()
            .Ok(Auth, "45.10.20.30", ms: 20)
            .Fails(Auth, "104.16.119.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected)
            .Ok(Auth, "104.16.120.50", ms: 90);
        return await new DiagnosticsEngine(dns, [doh], prober, new FakeProxy(), HostsDocument.Empty, TestOptions.Fast)
            .RunAsync([RiotServices.Find(Auth)!], null, CancellationToken.None);
    }

    [Fact]
    public async Task M1_address_known_only_from_system_dns_is_never_pinned_when_doh_is_blocked()
    {
        var report = await RunWithHostileSystemDns(new FakeTrustedResolver("Cloudflare DNS") { Unreachable = true });
        Assert.False(report.Plan.HasChanges);
    }

    [Fact]
    public async Task M1_only_doh_confirmed_addresses_are_pinned()
    {
        var report = await RunWithHostileSystemDns(new FakeTrustedResolver("Cloudflare DNS").Set(Auth, "104.16.120.50"));
        Assert.Equal("104.16.120.50", report.Plan.NewPins.Single().Address.ToString());
    }

    [Fact]
    public async Task M1_system_address_confirmed_by_doh_can_still_be_pinned()
    {
        var report = await RunWithHostileSystemDns(new FakeTrustedResolver("Cloudflare DNS").Set(Auth, "45.10.20.30"));
        Assert.Equal("45.10.20.30", report.Plan.NewPins.Single().Address.ToString());
    }

    // ---- M2: forged "disabled" comment lines must not be turned into active entries -------------------------------

    [Theory]
    [InlineData("10.0.0.66 www.paypal.com login.microsoftonline.com")]
    [InlineData("127.0.0.1 update.example-av.com")]
    [InlineData("10.0.0.66 auth.riotgames.com www.paypal.com")]
    public void M2_forged_disabled_lines_for_non_riot_names_are_never_activated(string forged)
    {
        var text = "127.0.0.1 localhost\r\n" + HostsDocument.MakeDisabled(forged) + "\r\n" +
                   HostsDocument.MakeDisabled(forged).Replace("# valdoctor-off:", "# riotfix-off:", StringComparison.Ordinal) + "\r\n";

        var apply = HostsDocument.Parse(Encoding.ASCII.GetBytes(text));
        apply.SetPins([AuthPin], disableConflicts: true);
        Assert.DoesNotContain(apply.GetForeignEntries(), e => e.LineText.Contains(forged, StringComparison.Ordinal) && !e.LineText.StartsWith('#'));

        var revert = HostsDocument.Parse(Encoding.ASCII.GetBytes(text));
        revert.RevertAll();
        Assert.Equal(text, revert.ToText());
    }

    [Fact]
    public void M2_genuine_disabled_riot_lines_are_still_restored()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes("5.6.7.8 auth.riotgames.com\r\n"));
        doc.SetPins([AuthPin], disableConflicts: true);
        var reverted = HostsDocument.Parse(doc.ToBytes());
        reverted.RevertAll();
        Assert.Equal("5.6.7.8 auth.riotgames.com\r\n", reverted.ToText());
    }

    // ---- M3: a failing ReplaceFile must never leave the system without a hosts file ------------------------------

    [Fact]
    public void M3_replace_failure_after_removing_hosts_restores_the_original_file()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Original);
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace")
            {
                // ERROR_UNABLE_TO_MOVE_REPLACEMENT: the replaced file is gone, the replacement keeps its temp name.
                fs.Remove(HostsPath);
                throw new IOException("Unable to move the replacement file.", unchecked((int)0x80070498));
            }
        };

        Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([AuthPin], false)));

        Assert.True(fs.Has(HostsPath));
        Assert.Equal(Original, fs.Get(HostsPath));
    }

    // ---- L1: a lock during Replace must not silently drop another program's concurrent change --------------------

    [Fact]
    public void L1_concurrent_change_during_a_locked_replace_is_preserved()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Original);
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        var once = true;
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace" && once)
            {
                once = false;
                fs.SetRaw(HostsPath, [.. Original, .. Encoding.ASCII.GetBytes("10.9.9.9 written-meanwhile.local\r\n")]);
                throw IoErrors.SharingViolation();
            }
        };

        Assert.True(service.Update(d => d.SetPins([AuthPin], false)).Written);
        var text = Encoding.ASCII.GetString(fs.Get(HostsPath));
        Assert.Contains("written-meanwhile.local", text, StringComparison.Ordinal);
        Assert.Contains("auth.riotgames.com", text, StringComparison.Ordinal);
    }

    // ---- L2: the "original" backup is written atomically ---------------------------------------------------------

    [Fact]
    public void L2_interrupted_original_backup_is_not_kept_as_the_original()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Original);
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        var crash = true;
        fs.BeforeOperation = (op, path) =>
        {
            if (crash && op == "write" && path.Contains("original", StringComparison.Ordinal))
            {
                crash = false;
                fs.SetRaw(path, [0x31]); // a truncated file is left behind by the "crash"
                throw new IOException("power loss");
            }
        };

        Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([AuthPin], false)));
        Assert.False(fs.Has(service.OriginalBackupPath));

        Assert.True(service.Update(d => d.SetPins([AuthPin], false)).Written);
        Assert.Equal(Original, fs.Get(service.OriginalBackupPath));
    }

    // ---- I1: a forged pin with a non-public address is not treated as ValDoctor's own ---------------------------

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public void I1_pin_line_with_non_public_address_is_foreign(string ip)
    {
        var line = HostsDocument.PinLineUnchecked(new HostPin(Auth, IPAddress.Parse(ip)));
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(line + "\r\n"));
        Assert.Empty(doc.GetPins());
        doc.RevertAll();
        Assert.Equal(line + "\r\n", doc.ToText());
    }

    // ---- I2: text from the update state file is cleaned before it is shown ---------------------------------------

    [Fact]
    public async Task I2_update_title_and_summary_from_the_state_file_are_cleaned_and_bounded()
    {
        var store = new MemoryStore(new UpdateState
        {
            LastCheckUtc = DateTimeOffset.UtcNow,
            LatestTag = "v0.9.0",
            LatestTitle = "Скачайте здесь: evil.example \u0007" + new string('x', 500),
            LatestSummary = new string('y', 2000) + "\r\nline2",
            LatestPrerelease = true,
        });
        var checker = new UpdateChecker(new HttpMessageInvoker(new FakeHttp((_, _) => throw new InvalidOperationException("no network expected"))),
            store, SemVersion.Create(0, 3, 0));

        var update = await checker.CheckAsync(CancellationToken.None);

        Assert.NotNull(update);
        Assert.True(update.Title.Length <= 80);
        Assert.True(update.Summary.Length <= 160);
        Assert.DoesNotContain('\u0007', update.Title);
        Assert.DoesNotContain('\n', update.Summary);
        Assert.Equal("https://github.com/IskhakovMA/riotfix/releases/tag/v0.9.0", update.Url.AbsoluteUri);
    }

    // ---- I3: special-purpose ranges are not public ---------------------------------------------------------------

    [Theory]
    [InlineData("3fff::1")]           // documentation, RFC 9637
    [InlineData("2001:2::1")]         // benchmarking, RFC 5180
    [InlineData("2001:10::1")]        // ORCHID
    [InlineData("2001:20::1")]        // ORCHIDv2
    [InlineData("192.31.196.1")]      // AS112-v4
    [InlineData("192.52.193.1")]      // AMT
    [InlineData("192.175.48.1")]      // direct delegation AS112
    public void I3_additional_special_purpose_ranges_are_not_public(string ip)
    {
        Assert.False(AddressPolicy.IsPublicUnicast(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("2606:4700::6810:ce83")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("104.16.120.50")]
    public void I3_real_service_addresses_stay_public(string ip) => Assert.True(AddressPolicy.IsPublicUnicast(IPAddress.Parse(ip)));

    // ---- L3/L4: elevated-process hardening that lives in project files ------------------------------------------

    [Fact]
    public void L3_dotnet_startup_hooks_are_disabled_and_no_dll_is_loaded_by_name_for_dns_flush()
    {
        var root = Root();
        var csproj = File.ReadAllText(Path.Join(root, "src", "ValDoctor.App", "ValDoctor.App.csproj"));
        Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", csproj, StringComparison.Ordinal);

        var services = File.ReadAllText(Path.Join(root, "src", "ValDoctor.App", "Services", "WindowsServices.cs"));
        Assert.DoesNotContain("LibraryImport(\"dnsapi.dll\")", services, StringComparison.Ordinal);
    }

    [Fact]
    public void L4_gui_running_as_administrator_writes_nothing_into_the_user_profile()
    {
        var program = File.ReadAllText(Path.Join(Root(), "src", "ValDoctor.App", "Program.cs"));
        Assert.Contains("ElevationService.IsAdministrator() ? NullLog.Instance", program, StringComparison.Ordinal);
        var vm = File.ReadAllText(Path.Join(Root(), "src", "ValDoctor.App", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("ElevationService.IsAdministrator()", vm[vm.IndexOf("public UpdateChecker CreateUpdateChecker()", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "ValDoctor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private sealed class MemoryStore(UpdateState state) : IUpdateStateStore
    {
        private UpdateState _state = state;

        public UpdateState Load() => _state;

        public void Save(UpdateState state) => _state = state;
    }
}
