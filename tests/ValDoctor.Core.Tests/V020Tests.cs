using System.Net;
using System.Text;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Network;
using ValDoctor.Core.Tests.Fakes;
using ValDoctor.Core.Updates;

namespace ValDoctor.Core.Tests;

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Domain classification for multi-address hosts and partially working setups (v0.2.0).</summary>
public class ClassificationTests
{
    private const string Auth = "auth.riotgames.com";
    private const string Account = "account.riotgames.com";
    private const string Data = "data.riotgames.com";
    private const string Config = "clientconfig.rpg.riotgames.com";

    private readonly FakeSystemResolver _dns = new();
    private readonly FakeTrustedResolver _cloudflare = new("Cloudflare DNS");
    private readonly FakeTrustedResolver _google = new("Google Public DNS");
    private readonly FakeProber _prober = new();

    private Task<DiagnosisReport> Run(params string[] hosts) =>
        new DiagnosticsEngine(_dns, [_cloudflare, _google], _prober, new FakeProxy(), HostsDocument.Empty, TestOptions.Fast)
            .RunAsync(hosts.Select(h => RiotServices.Find(h)!).ToList(), null, CancellationToken.None);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_address_tls_timeout_other_works_is_degraded_regardless_of_dns_order(bool goodFirst)
    {
        // DNS round-robin rotates the order; 0.1.0 called this "healthy" whenever the good address came first.
        if (goodFirst)
        {
            _dns.Set(Auth, "104.16.120.50", "104.16.119.50");
        }
        else
        {
            _dns.Set(Auth, "104.16.119.50", "104.16.120.50");
        }

        _prober.Ok(Auth, "104.16.120.50").Fails(Auth, "104.16.119.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Auth, "104.16.120.50", "104.16.119.50"); // security audit M1: pins need a trusted-resolver source

        var report = await Run(Auth);

        Assert.Equal(DomainHealth.Degraded, report.Domains[0].Health);
        Assert.Equal("104.16.120.50", report.Plan.NewPins.Single().Address.ToString());
    }

    [Theory]
    [InlineData(ProbeFailure.TcpRefused)]
    [InlineData(ProbeFailure.TcpReset)]
    [InlineData(ProbeFailure.NetworkUnreachable)]
    public async Task Instant_connect_failure_on_one_address_is_windows_fallback_not_a_problem(ProbeFailure failure)
    {
        _dns.Set(Auth, "104.16.119.50", "104.16.120.50");
        _prober.Fails(Auth, "104.16.119.50", failure).Ok(Auth, "104.16.120.50");

        var report = await Run(Auth);

        Assert.Equal(DomainHealth.PartiallyReachable, report.Domains[0].Health);
        Assert.True(report.Domains[0].IsReachable);
        Assert.Equal(Verdict.AllHealthy, report.Verdict);
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(0, _cloudflare.Calls);
    }

    [Theory]
    [InlineData("10.10.34.35")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public async Task Non_public_dns_answer_next_to_a_good_address_is_not_reported_as_ok(string injected)
    {
        // Typical ISP block page injection: never connected to, never treated as harmless.
        _dns.Set(Auth, injected, "104.16.120.50");
        _prober.Ok(Auth, "104.16.120.50");
        _cloudflare.Set(Auth, "104.16.120.50"); // security audit M1: pins need a trusted-resolver source

        var report = await Run(Auth);

        Assert.Equal(DomainHealth.Degraded, report.Domains[0].Health);
        Assert.NotEqual(Verdict.AllHealthy, report.Verdict);
        Assert.Equal("104.16.120.50", report.Plan.NewPins.Single().Address.ToString());
        Assert.DoesNotContain(_prober.Probed, k => k.EndsWith("|" + injected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    public async Task Http_client_errors_are_not_network_faults(int status)
    {
        _dns.Set(Auth, "104.16.120.50");
        _prober.Ok(Auth, "104.16.120.50", status: status);
        var report = await Run(Auth);
        Assert.Equal(DomainHealth.Healthy, report.Domains[0].Health);
        Assert.False(report.Plan.HasChanges);
    }

    [Fact]
    public async Task Real_pc_state_two_auxiliary_services_down_without_fix_is_limited_not_critical()
    {
        // October 2026, real affected PC: account + data TLS timeout, sign-in and VALORANT worked.
        _dns.Set(Auth, "104.16.120.50").Set(Config, "104.18.157.37").Set(Account, "104.16.56.40").Set(Data, "104.16.56.40");
        _prober.Ok(Auth, "104.16.120.50").Ok(Config, "104.18.157.37")
            .Fails(Account, "104.16.56.40", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected)
            .Fails(Data, "104.16.56.40", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Account, "104.16.56.40").Set(Data, "104.16.56.40");
        _google.Set(Account, "104.16.56.40").Set(Data, "104.16.56.40");

        var report = await Run(Auth, Account, Config, Data);

        Assert.Equal(Verdict.LimitedConnectivity, report.Verdict);
        Assert.True(report.EssentialReachable);
        Assert.False(report.Plan.HasChanges);
        // Not a red error: the main services work. Plain wording, no promise that the game works.
        var message = Texts.ForReport(report);
        Assert.Equal(StatusTone.Success, message.Tone);
        Assert.Equal("Подключение работает", message.Headline);
        Assert.Contains("вход и игра при этом могут работать", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("гарантир", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("невозможен", message.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_fix_text_names_the_real_reason()
    {
        _dns.Set(Auth, "104.16.120.50");
        _prober.Fails(Auth, "104.16.120.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Auth, "104.16.119.50");
        _prober.Ok(Auth, "104.16.119.50");
        var engine = new DiagnosticsEngine(_dns, [_cloudflare], _prober, new FakeProxy(),
            () => HostsDocument.Parse(Encoding.ASCII.GetBytes("104.16.120.50 auth.riotgames.com example.com\r\n")), TestOptions.Fast);

        var report = await engine.RunAsync([RiotServices.Find(Auth)!], null, CancellationToken.None);

        Assert.Equal(Verdict.ProblemsWithoutFix, report.Verdict);
        Assert.Contains("чужие правила", Texts.ForReport(report).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_essential_services_down_without_fix_is_a_problem()
    {
        _dns.Set(Auth, "104.16.120.50").Set(Config, "104.18.157.37").Set(Account, "104.16.56.40");
        _prober.Ok(Account, "104.16.56.40")
            .Fails(Auth, "104.16.120.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected)
            .Fails(Config, "104.18.157.37", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);

        var report = await Run(Auth, Account, Config);

        Assert.Equal(Verdict.ProblemsWithoutFix, report.Verdict);
        Assert.False(report.Plan.HasChanges);
        var message = Texts.ForReport(report);
        Assert.Equal("Не удалось найти безопасное исправление", message.Headline);
        Assert.Contains("Вход в игру при этом может работать", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Problem_host_without_verified_alternatives_never_produces_a_plan()
    {
        _dns.Set(Auth, "104.16.120.50");
        _prober.Fails(Auth, "104.16.120.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Auth, "104.16.120.50", "104.16.119.50");
        _prober.Flaky(Auth, "104.16.119.50");

        var report = await Run(Auth);

        Assert.False(report.Plan.HasChanges);
        Assert.Empty(report.Plan.DesiredPins);
    }

    [Fact]
    public async Task Auxiliary_fix_is_offered_but_marked_optional()
    {
        _dns.Set(Auth, "104.16.120.50").Set(Data, "104.16.56.40");
        _prober.Ok(Auth, "104.16.120.50").Fails(Data, "104.16.56.40", ProbeFailure.TcpTimeout);
        _cloudflare.Set(Data, "104.16.55.40");
        _prober.Ok(Data, "104.16.55.40");

        var report = await Run(Auth, Data);

        Assert.Equal(Verdict.FixAvailable, report.Verdict);
        Assert.Contains("не обязательно", Texts.ForReport(report).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_records_hosts_fingerprint()
    {
        _dns.Set(Auth, "104.16.120.50");
        _prober.Ok(Auth, "104.16.120.50");
        var report = await Run(Auth);
        Assert.Equal(HostsFingerprint.Of(HostsDocument.Empty()), report.HostsFingerprint);
    }

    [Fact]
    public void After_fix_with_essential_services_up_is_success_and_does_not_push_rollback()
    {
        var after = new DiagnosisReport(DateTimeOffset.Now, TimeSpan.Zero,
        [
            Domain(Auth, DomainHealth.Healthy),
            Domain(Config, DomainHealth.Healthy),
            Domain(Account, DomainHealth.Failed),
            Domain(Data, DomainHealth.Failed),
        ], false, true, true, [], FixPlan.Empty, Verdict.LimitedConnectivity);

        var message = Texts.AfterFix(after, [new HostPin(Auth, IPAddress.Parse("104.16.120.50"))]);

        Assert.Equal(StatusTone.Success, message.Tone);
        Assert.Equal("Перезапустите Riot Client и попробуйте войти.", message.Body);
        Assert.DoesNotContain("Отменить изменения", message.Body, StringComparison.Ordinal);
    }

    internal static DomainDiagnosis Domain(string host, DomainHealth health) =>
        new(RiotServices.Find(host)!, new SystemDnsAnswer([IPAddress.Parse("104.16.1.1")], DnsFailureKind.None), [], health, HostsOverride.None, [], []);
}

/// <summary>Re-validation right before UAC (v0.2.0).</summary>
public class FixPreflightTests
{
    private const string Auth = "auth.riotgames.com";
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeSystemResolver _dns = new();
    private readonly FakeTrustedResolver _cloudflare = new("Cloudflare DNS");
    private readonly FakeProber _prober = new();
    private readonly ManualTime _time = new(T0);
    private HostsDocument _hosts = HostsDocument.Empty();
    private int _diagnoses;

    private DiagnosticsEngine Engine() => new(_dns, [_cloudflare], _prober, new FakeProxy(), () => _hosts, TestOptions.Fast);

    private async Task<DiagnosisReport> Diagnose(CancellationToken ct)
    {
        _diagnoses++;
        var report = await Engine().RunAsync([RiotServices.Find(Auth)!], null, ct);
        return report with { StartedAt = _time.Now };
    }

    private FixPreflight Preflight() => new(Diagnose, _prober, () => _hosts, _time);

    private async Task<DiagnosisReport> Initial()
    {
        _dns.Set(Auth, "104.16.119.50");
        _prober.Fails(Auth, "104.16.119.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Auth, "104.16.120.50");
        _prober.Ok(Auth, "104.16.120.50");
        var report = await Diagnose(CancellationToken.None);
        Assert.True(report.Plan.HasChanges);
        _diagnoses = 0;
        return report;
    }

    [Fact]
    public async Task Fresh_result_with_working_addresses_is_ready_without_new_diagnosis()
    {
        var report = await Initial();
        _time.Now = T0.AddMinutes(1);

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.Ready, result.Status);
        Assert.Same(report, result.Report);
        Assert.Equal(0, _diagnoses);
        Assert.Equal(3, _prober.CallsTo(Auth, "104.16.120.50")); // 2 in diagnosis + 1 re-check
    }

    [Fact]
    public async Task Outdated_result_triggers_a_new_diagnosis()
    {
        var report = await Initial();
        _time.Now = T0.AddMinutes(30);

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, _diagnoses);
        Assert.Equal(PreflightStatus.Ready, result.Status);
        Assert.NotSame(report, result.Report);
    }

    [Fact]
    public async Task Address_that_stopped_working_before_uac_is_replaced_and_must_be_reviewed()
    {
        var report = await Initial();
        _prober.Fails(Auth, "104.16.120.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);
        _cloudflare.Set(Auth, "104.16.120.50", "104.16.121.50");
        _prober.Ok(Auth, "104.16.121.50");

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.PlanChanged, result.Status);
        Assert.Equal("104.16.121.50", result.Report.Plan.NewPins.Single().Address.ToString());
    }

    [Fact]
    public async Task Address_that_stopped_working_without_alternative_means_no_write()
    {
        var report = await Initial();
        _prober.Fails(Auth, "104.16.120.50", ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);
        Assert.False(result.Report.Plan.HasChanges);
    }

    [Fact]
    public async Task Problem_that_went_away_means_no_write()
    {
        var report = await Initial();
        _time.Now = T0.AddMinutes(10);
        _prober.Ok(Auth, "104.16.119.50");

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);
    }

    [Fact]
    public async Task Hosts_change_since_diagnosis_triggers_a_new_diagnosis_with_conflicts()
    {
        var report = await Initial();
        _hosts = HostsDocument.Parse(Encoding.ASCII.GetBytes("1.2.3.4 auth.riotgames.com example.com\r\n"));

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, _diagnoses);
        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);
        Assert.Equal(UnfixableReason.ConflictCannotBeDisabled, result.Report.Plan.Unfixable.Single().Reason);
    }

    [Fact]
    public async Task Prober_exception_during_preflight_is_treated_as_a_dead_address()
    {
        var report = await Initial();
        _prober.On(Auth, "104.16.120.50", (_, _) => throw new ObjectDisposedException("socket"));

        var result = await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, _diagnoses);
        Assert.NotEqual(PreflightStatus.Ready, result.Status);
    }

    [Fact]
    public async Task Clock_moving_backwards_makes_the_result_stale()
    {
        var report = await Initial();
        _time.Now = T0.AddHours(-2);

        await Preflight().CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, _diagnoses);
    }

    [Fact]
    public async Task Unreadable_hosts_triggers_a_new_diagnosis()
    {
        var report = await Initial();
        var preflight = new FixPreflight(Diagnose, _prober, () => throw new HostsAccessException(HostsErrorKind.AccessDenied, "denied"), _time);

        await preflight.CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, _diagnoses);
    }
}

public class SemVersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("v0.2.0", "0.2.0", 0)]
    [InlineData("0.2.0", "0.2.0-rc.1", 1)]
    [InlineData("0.2.0-rc.2", "0.2.0-rc.1", 1)]
    [InlineData("0.2.0-rc.10", "0.2.0-rc.2", 1)]
    [InlineData("0.2.0-beta", "0.2.0-alpha", 1)]
    [InlineData("0.2.0-alpha.1", "0.2.0-alpha", 1)]
    [InlineData("0.2.0-alpha.beta", "0.2.0-alpha.1", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("0.2.0+build.5", "0.2.0", 0)]
    public void Compares_by_semver_rules(string a, string b, int expected)
    {
        Assert.True(SemVersion.TryParse(a, out var va));
        Assert.True(SemVersion.TryParse(b, out var vb));
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-expected, Math.Sign(vb.CompareTo(va)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("latest")]
    [InlineData("1.2.3-<script>")]
    public void Rejects_invalid_versions(string text) => Assert.False(SemVersion.TryParse(text, out _));
}

public class UpdateCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemoryStore _store = new();

    private static string Release(string tag, bool prerelease = false, bool draft = false, string? htmlUrl = null, string body = "## Что нового\n* Улучшена диагностика") =>
        $$"""{"tag_name":"{{tag}}","name":"RiotFix {{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"{{htmlUrl ?? "https://github.com/IskhakovMA/riotfix/releases/tag/" + tag}}","body":{{System.Text.Json.JsonSerializer.Serialize(body)}}}""";

    private UpdateChecker Checker(FakeHttp http, string current = "0.2.0") =>
        new(new HttpMessageInvoker(http), _store, Parse(current), _time, timeout: TimeSpan.FromMilliseconds(300));

    private static SemVersion Parse(string v) => SemVersion.TryParse(v, out var s) ? s : throw new ArgumentException(v);

    [Fact]
    public async Task Github_unavailable_is_silent()
    {
        var http = FakeHttp.Throws(new HttpRequestException("offline"));
        Assert.Null(await Checker(http).CheckAsync(CancellationToken.None));
        Assert.Null(_store.State.LastCheckUtc); // retried on the next launch
    }

    [Fact]
    public async Task Github_timeout_is_silent()
    {
        var http = FakeHttp.Hangs();
        Assert.Null(await Checker(http).CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Github_error_status_is_silent(HttpStatusCode status)
    {
        Assert.Null(await Checker(FakeHttp.Status(status)).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Newer_version_is_offered_with_official_link_and_summary()
    {
        var http = FakeHttp.Json($"[{Release("v0.3.0", prerelease: true)},{Release("v0.2.0", prerelease: true)},{Release("v0.1.0", prerelease: true)}]");

        var update = await Checker(http).CheckAsync(CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update.Version.ToString());
        Assert.Equal("https://github.com/IskhakovMA/riotfix/releases/tag/v0.3.0", update.Url.AbsoluteUri);
        Assert.Equal("Что нового", update.Summary);
    }

    [Fact]
    public async Task Latest_version_installed_offers_nothing()
    {
        var http = FakeHttp.Json($"[{Release("v0.2.0", prerelease: true)},{Release("v0.1.0", prerelease: true)}]");
        Assert.Null(await Checker(http).CheckAsync(CancellationToken.None));
        Assert.Equal(T0, _store.State.LastCheckUtc);
    }

    [Fact]
    public async Task Prerelease_of_current_version_is_older_than_the_release()
    {
        var http = FakeHttp.Json($"[{Release("v0.2.0-rc.1", prerelease: true)}]");
        Assert.Null(await Checker(http).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Newer_prerelease_is_offered_to_0x_builds_but_not_to_stable_builds()
    {
        var json = $"[{Release("v1.1.0-beta.1", prerelease: true)},{Release("v1.0.0")}]";
        Assert.Equal("1.1.0-beta.1", (await Checker(FakeHttp.Json(json), "0.2.0").CheckAsync(CancellationToken.None))!.Version.ToString());

        _store.State = new UpdateState();
        Assert.Null(await Checker(FakeHttp.Json(json), "1.0.0").CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Checks_at_most_once_per_24_hours_but_keeps_showing_cached_update()
    {
        var http = FakeHttp.Json($"[{Release("v0.3.0", prerelease: true)}]");
        Assert.NotNull(await Checker(http).CheckAsync(CancellationToken.None));

        _time.Now = T0.AddHours(23);
        Assert.NotNull(await Checker(http).CheckAsync(CancellationToken.None));
        Assert.Equal(1, http.Calls);

        _time.Now = T0.AddHours(25);
        await Checker(http).CheckAsync(CancellationToken.None);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task Dismissed_version_is_not_shown_again_but_a_newer_one_is()
    {
        var checker = Checker(FakeHttp.Json($"[{Release("v0.3.0", prerelease: true)}]"));
        var update = await checker.CheckAsync(CancellationToken.None);
        checker.Dismiss(update!);
        Assert.Null(await checker.CheckAsync(CancellationToken.None));

        _time.Now = T0.AddDays(2);
        var newer = Checker(FakeHttp.Json($"[{Release("v0.4.0", prerelease: true)},{Release("v0.3.0", prerelease: true)}]"));
        Assert.Equal("0.4.0", (await newer.CheckAsync(CancellationToken.None))!.Version.ToString());
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var huge = "[" + string.Join(',', Enumerable.Repeat(Release("v0.3.0", body: new string('x', 4000)), 200)) + "]";
        Assert.True(huge.Length > UpdateChecker.MaxResponseBytes);
        Assert.Null(await Checker(FakeHttp.Json(huge)).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_json_is_silent()
    {
        Assert.Null(await Checker(FakeHttp.Json("{not json")).CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("https://evil.example/riotfix/releases/tag/v0.3.0")]
    [InlineData("https://github.com/someone-else/riotfix/releases/tag/v0.3.0")]
    [InlineData("javascript:alert(1)")]
    public void Releases_with_foreign_links_are_ignored(string htmlUrl)
    {
        var releases = UpdateChecker.ParseReleases(Encoding.UTF8.GetBytes($"[{Release("v0.3.0", htmlUrl: htmlUrl)}]"));
        Assert.Empty(releases);
    }

    [Fact]
    public void Drafts_and_non_semver_tags_are_ignored()
    {
        var releases = UpdateChecker.ParseReleases(Encoding.UTF8.GetBytes($"[{Release("v0.3.0", draft: true)},{Release("nightly")}]"));
        Assert.Empty(releases);
    }

    [Fact]
    public async Task Corrupt_state_file_does_not_break_the_check()
    {
        var dir = Directory.CreateTempSubdirectory("riotfix-upd-");
        try
        {
            var path = Path.Join(dir.FullName, "update-state.json");
            await File.WriteAllTextAsync(path, "{garbage", TestContext.Current.CancellationToken);
            var checker = new UpdateChecker(new HttpMessageInvoker(FakeHttp.Json($"[{Release("v0.3.0", prerelease: true)}]")),
                new FileUpdateStateStore(path), Parse("0.2.0"), _time);

            Assert.NotNull(await checker.CheckAsync(CancellationToken.None));
            Assert.Contains("v0.3.0", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class MemoryStore : IUpdateStateStore
    {
        public UpdateState State { get; set; } = new();

        public UpdateState Load() => State;

        public void Save(UpdateState state) => State = state;
    }
}

internal sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public int Calls { get; private set; }

    public static FakeHttp Json(string json) => new((request, _) =>
    {
        Assert.Equal("api.github.com", request.RequestUri!.Host);
        Assert.Contains("ValDoctor", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    });

    public static FakeHttp Status(HttpStatusCode status) => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    public static FakeHttp Throws(Exception ex) => new((_, _) => Task.FromException<HttpResponseMessage>(ex));

    public static FakeHttp Hangs() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return handler(request, cancellationToken);
    }
}

/// <summary>Compatibility with hosts files written by RiotFix 0.1.0, and the hosts path policy.</summary>
public class CompatibilityAndBoundaryTests
{
    // Exactly what RiotFix 0.1.0 wrote (format frozen; do not regenerate from current code).
    private const string V010File =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "127.0.0.1 router.local\r\n" +
        "# riotfix-off:d71553f8 5.6.7.8 auth.riotgames.com\r\n" +
        "# >>> RiotFix BEGIN (managed block, https://github.com/IskhakovMA/riotfix)\r\n" +
        "104.16.120.50 auth.riotgames.com # riotfix:fd5fb184\r\n" +
        "104.18.157.37 clientconfig.rpg.riotgames.com # riotfix:9f082cf2\r\n" +
        "# <<< RiotFix END\r\n" +
        "10.0.0.9 added-by-user-later.local\r\n";

    [Fact]
    public void Blocks_written_by_0_1_0_are_recognised_and_fully_reverted()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(V010File));

        Assert.Equal(2, doc.GetPins().Count);
        Assert.Single(doc.GetDisabledLines());

        doc.RevertAll();
        Assert.Equal(
            "# Copyright (c) 1993-2009 Microsoft Corp.\r\n127.0.0.1 router.local\r\n5.6.7.8 auth.riotgames.com\r\n10.0.0.9 added-by-user-later.local\r\n",
            doc.ToText());
    }

    [Fact]
    public void Blocks_written_by_0_1_0_are_updated_in_place_without_duplicates()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(V010File));
        doc.SetPins([new HostPin("auth.riotgames.com", IPAddress.Parse("104.16.119.50"))], disableConflicts: false);

        // The RiotFix block is rewritten in place in the ValDoctor format: no second block, no leftovers.
        var text = doc.ToText();
        Assert.Single(doc.GetPins());
        Assert.Equal(0, CountOf(text, "RiotFix BEGIN"));
        Assert.Equal(0, CountOf(text, "# riotfix:"));
        Assert.Equal(1, CountOf(text, "ValDoctor BEGIN"));
        Assert.EndsWith("# <<< ValDoctor END\r\n10.0.0.9 added-by-user-later.local\r\n", text, StringComparison.Ordinal);
        // The third-party line RiotFix disabled stays disabled (its host is still pinned) and remains restorable.
        Assert.Contains("# riotfix-off:d71553f8 5.6.7.8 auth.riotgames.com", text, StringComparison.Ordinal);
        Assert.Single(doc.GetDisabledLines());
    }

    [Fact]
    public void Partial_revert_of_0_1_0_block_keeps_the_rest()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(V010File));
        doc.Revert(["clientconfig.rpg.riotgames.com"]);
        Assert.Equal(["auth.riotgames.com"], doc.GetPins().Select(p => p.Host));
        Assert.Single(doc.GetDisabledLines());
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\drivers\etc", "standard")]
    [InlineData(@"%systemroot%\system32\drivers\etc\", "standard")]
    [InlineData(null, "standard")]
    [InlineData("", "standard")]
    [InlineData(@"%USERPROFILE%\etc", "standard")]
    [InlineData(@"%SystemRoot%\..\Users\x\etc", "standard")]
    [InlineData(@"\\server\share\etc", "standard")]
    [InlineData(@"\\?\C:\evil", "standard")]
    [InlineData(@"relative\etc", "standard")]
    [InlineData(@"C:\Windows\..\Users\x", "standard")]
    [InlineData(@"D:\Windows\System32\drivers\etc", @"D:\Windows\System32\drivers\etc\hosts")]
    public void Hosts_path_never_expands_user_environment(string? raw, string expected)
    {
        const string system = @"C:\Windows\System32";
        var standard = Path.Join(system, "drivers", "etc", "hosts");
        Assert.Equal(expected == "standard" ? standard : expected, HostsPathPolicy.Resolve(raw, system));
    }

    [Fact]
    public void Every_native_import_in_the_app_is_restricted_to_system32()
    {
        var root = RepoRoot();
        var sources = Directory.GetFiles(Path.Join(root, "src", "ValDoctor.App"), "*.cs", SearchOption.AllDirectories);
        var imports = 0;
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("[LibraryImport(", StringComparison.Ordinal) || lines[i].Contains("[DllImport(", StringComparison.Ordinal))
                {
                    imports++;
                    Assert.True(i + 1 < lines.Length && lines[i + 1].Contains("DefaultDllImportSearchPaths(DllImportSearchPath.System32)", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)}:{i + 1} native import without System32-only search path");
                }
            }
        }

        Assert.True(imports >= 3); // kernel32 (2) + dwmapi; dnsapi is no longer imported (ipconfig by absolute path)
    }

    [Fact]
    public void Elevated_helper_hardens_dll_search_before_touching_hosts()
    {
        var program = File.ReadAllText(Path.Join(RepoRoot(), "src", "ValDoctor.App", "Program.cs"));
        var branch = program.IndexOf("IsElevatedInvocation(args)", StringComparison.Ordinal);
        var harden = program.IndexOf("RestrictDllSearchToSystem32()", StringComparison.Ordinal);
        var hosts = program.IndexOf("new HostsFileService(", StringComparison.Ordinal);
        Assert.True(branch > 0 && harden > branch && hosts > harden);
        Assert.Contains("HostsLocator.GetHostsPath()", program, StringComparison.Ordinal);

        var locator = File.ReadAllText(Path.Join(RepoRoot(), "src", "ValDoctor.App", "Services", "WindowsServices.cs"));
        Assert.Contains("RegistryValueOptions.DoNotExpandEnvironmentNames", locator, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandEnvironmentVariables", locator, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "ValDoctor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
