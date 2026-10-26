using RiotFix.Core.Diagnostics;
using RiotFix.Core.Hosts;
using RiotFix.Core.Network;
using RiotFix.Core.Tests.Fakes;

namespace RiotFix.Core.Tests;

/// <summary>Short-lived DNS outages must not lead to hosts changes (real log, 26.10.2026 14:28).</summary>
public class TransientDnsTests
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    private readonly FakeSystemResolver _dns = new();
    private readonly FakeTrustedResolver _cloudflare = new("Cloudflare DNS");
    private readonly FakeProber _prober = new();

    private DiagnosticsEngine Engine(DiagnosticsOptions? options = null) =>
        new(_dns, [_cloudflare], _prober, new FakeProxy(), HostsDocument.Empty,
            options ?? new DiagnosticsOptions { DnsRetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)] });

    private void AllSixWork()
    {
        var i = 0;
        foreach (var s in RiotServices.All)
        {
            var ip = $"104.16.{100 + i++}.50";
            _dns.Set(s.Host, ip);
            _prober.Ok(s.Host, ip);
            _cloudflare.Set(s.Host, ip);
        }
    }

    [Fact]
    public async Task Mass_dns_timeout_that_recovers_within_seconds_proposes_no_fix()
    {
        AllSixWork();
        foreach (var s in RiotServices.All)
        {
            _dns.FailFirst(s.Host, 1);
        }

        var report = await Engine().RunAsync(RiotServices.All, null, CancellationToken.None);

        Assert.Equal(Verdict.AllHealthy, report.Verdict);
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(0, _cloudflare.Calls);
        Assert.All(RiotServices.All, s => Assert.Equal(2, _dns.CallsTo(s.Host)));
        Assert.All(report.Domains, d => Assert.True(d.DnsRetried));
    }

    [Fact]
    public async Task Persistent_dns_failure_still_gets_a_verified_fix()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);

        var report = await Engine().RunAsync(RiotServices.All, null, CancellationToken.None);

        Assert.Equal(Verdict.FixAvailable, report.Verdict);
        var candidate = report.Domains.Single(d => d.Host == "auth.riotgames.com").BestCandidate!;
        Assert.True(candidate.Verified);
        Assert.Equal("auth.riotgames.com", report.Plan.NewPins.Single().Host);
        Assert.Equal(3, _dns.CallsTo("auth.riotgames.com")); // first try + 2 retries, no more
    }

    [Fact]
    public async Task Dns_answer_that_works_is_never_retried()
    {
        AllSixWork();
        await Engine().RunAsync(RiotServices.All, null, CancellationToken.None);
        Assert.All(RiotServices.All, s => Assert.Equal(1, _dns.CallsTo(s.Host)));
    }

    [Fact]
    public async Task Dns_recovery_before_uac_drops_the_fix_and_leaves_hosts_untouched()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 3);
        var engine = Engine();
        var report = await engine.RunAsync(RiotServices.All, null, CancellationToken.None);
        Assert.True(report.Plan.HasChanges);

        // Moments later the system DNS works again: the preflight must notice it and drop the fix.
        var diagnoses = 0;
        var preflight = new FixPreflight(ct => { diagnoses++; return engine.RunAsync(RiotServices.All, null, ct); }, _prober, HostsDocument.Empty, Later(report));
        var result = await preflight.CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, diagnoses);
        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);

        // Even if the elevated helper were invoked with what is left, the hosts file would not change.
        var fs = new FakeFileSystem();
        var original = System.Text.Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n");
        fs.Seed(HostsPath, original);
        var hosts = new HostsFileService(fs, HostsPath, RiotFix.Core.Logging.NullLog.Instance, _ => { });
        var code = RiotFix.Core.Elevation.ElevatedRunner.Run(
            RiotFix.Core.Elevation.ElevatedProtocol.ToArguments(RiotFix.Core.Elevation.ElevatedRequest.Apply(result.Report.Plan.DesiredPins, false)),
            hosts, () => { }, RiotFix.Core.Logging.NullLog.Instance);
        Assert.Equal(RiotFix.Core.Elevation.ElevatedExitCode.NoChanges, code);
        Assert.Equal(original, fs.Get(HostsPath));
    }

    [Fact]
    public async Task Dns_still_failing_during_preflight_keeps_the_same_verified_fix()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);
        var engine = Engine();
        var report = await engine.RunAsync(RiotServices.All, null, CancellationToken.None);
        var pin = report.Plan.NewPins.Single();
        var probesBefore = _prober.CallsTo(pin.Host, pin.Address.ToString());

        var result = await new FixPreflight(ct => engine.RunAsync(RiotServices.All, null, ct), _prober, HostsDocument.Empty, Later(report))
            .CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.Ready, result.Status);
        Assert.Equal(pin, result.Report.Plan.NewPins.Single());
        // The address was verified again end-to-end (twice) by the fresh diagnosis.
        Assert.Equal(probesBefore + 2, _prober.CallsTo(pin.Host, pin.Address.ToString()));
        Assert.Equal(6, _dns.CallsTo("auth.riotgames.com"));
    }

    [Fact]
    public async Task Address_failing_during_preflight_after_dns_failure_is_not_written()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);
        var engine = Engine();
        var report = await engine.RunAsync(RiotServices.All, null, CancellationToken.None);
        var pin = report.Plan.NewPins.Single();
        _prober.Fails(pin.Host, pin.Address.ToString(), ProbeFailure.TlsTimeout, ProbeStage.TcpConnected);

        var result = await new FixPreflight(ct => engine.RunAsync(RiotServices.All, null, ct), _prober, HostsDocument.Empty, Later(report))
            .CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);
        Assert.False(result.Report.Plan.HasChanges);
    }

    [Fact]
    public async Task Second_preflight_right_after_a_fresh_diagnosis_only_reprobes_the_addresses()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);
        var engine = Engine();
        var report = await engine.RunAsync(RiotServices.All, null, CancellationToken.None);
        var pin = report.Plan.NewPins.Single();
        var probesBefore = _prober.CallsTo(pin.Host, pin.Address.ToString());
        var diagnoses = 0;

        // Same moment the report was finished (e.g. the first preflight just rebuilt it).
        var now = new ManualTime(report.StartedAt + report.Duration + TimeSpan.FromSeconds(2));
        var result = await new FixPreflight(ct => { diagnoses++; return engine.RunAsync(RiotServices.All, null, ct); }, _prober, HostsDocument.Empty, now)
            .CheckAsync(report, CancellationToken.None);

        Assert.Equal(PreflightStatus.Ready, result.Status);
        Assert.Equal(0, diagnoses);
        Assert.Equal(probesBefore + 1, _prober.CallsTo(pin.Host, pin.Address.ToString()));
    }

    /// <summary>The user read the result and clicked «Исправить подключение» half a minute later.</summary>
    private static ManualTime Later(DiagnosisReport report) => new(report.StartedAt + report.Duration + TimeSpan.FromSeconds(30));

    [Fact]
    public async Task User_cancel_during_dns_retry_stops_quickly()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);
        var engine = Engine(new DiagnosticsOptions { DnsRetryDelays = [TimeSpan.FromMinutes(5)] });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(RiotServices.All, null, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}

/// <summary>The configuration used in tests must not silently differ from what the application really runs.</summary>
public class AppCompositionTests
{
    [Fact]
    public void Product_options_retry_system_dns_twice_after_2_and_3_seconds()
    {
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], DiagnosticsOptions.Product.DnsRetryDelays);
        var fresh = new DiagnosticsOptions();
        Assert.Equal(fresh with { DnsRetryDelays = DiagnosticsOptions.Product.DnsRetryDelays }, DiagnosticsOptions.Product);
        Assert.Equal(fresh.DnsRetryDelays, DiagnosticsOptions.Product.DnsRetryDelays);
    }

    [Fact]
    public void Default_engine_uses_product_options()
    {
        var engine = DiagnosticsEngine.CreateDefault(new FakeSystemResolver(), [], new FakeProber(), new FakeProxy(), HostsDocument.Empty,
            RiotFix.Core.Logging.NullLog.Instance);
        Assert.Same(DiagnosticsOptions.Product, engine.Options);
    }

    [Fact]
    public void Test_options_differ_from_product_only_in_dns_retry_delays()
    {
        Assert.Equal(DiagnosticsOptions.Product with { DnsRetryDelays = TestOptions.Fast.DnsRetryDelays }, TestOptions.Fast);
        Assert.Equal(DiagnosticsOptions.Product.DnsRetryDelays.Count, TestOptions.Fast.DnsRetryDelays.Count);
    }

    [Fact]
    public void App_creates_the_engine_through_the_default_factory_and_never_flushes_dns_while_checking()
    {
        var vm = File.ReadAllText(Path.Join(RepoRoot(), "src", "RiotFix.App", "ViewModels", "MainViewModel.cs"));
        var create = vm[vm.IndexOf("public DiagnosticsEngine CreateEngine()", StringComparison.Ordinal)..];
        create = create[..create.IndexOf(';', StringComparison.Ordinal)];
        Assert.Contains("DiagnosticsEngine.CreateDefault(", create, StringComparison.Ordinal);
        Assert.DoesNotContain("new DiagnosticsOptions", vm, StringComparison.Ordinal);

        // Every diagnosis in the app (check, preflight, re-check, self-check) goes through CreateEngine.
        Assert.DoesNotContain("new DiagnosticsEngine(", vm, StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Join(RepoRoot(), "src", "RiotFix.App"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("new DiagnosticsEngine(", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.Contains("CreatePreflight(RunDiagnosisAsync)", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnosis_has_no_hook_to_change_system_state()
    {
        // The engine cannot be handed a callback (such as a DNS cache flush) to run while checking.
        foreach (var ctor in typeof(DiagnosticsEngine).GetConstructors())
        {
            Assert.DoesNotContain(ctor.GetParameters(), p => typeof(Delegate).IsAssignableFrom(p.ParameterType) && p.ParameterType != typeof(Func<HostsDocument?>));
        }
    }

    [Fact]
    public void Dns_cache_is_flushed_only_after_a_consented_hosts_change()
    {
        var vm = File.ReadAllText(Path.Join(RepoRoot(), "src", "RiotFix.App", "ViewModels", "MainViewModel.cs"));
        foreach (var method in new[] { "private async Task CheckAsync()", "private async Task<DiagnosisReport?> PreflightAsync()", "private async Task<DiagnosisReport> RunDiagnosisAsync(" })
        {
            var body = Body(vm, method);
            Assert.DoesNotContain("FlushDnsAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DnsCache.Flush", body, StringComparison.Ordinal);
        }

        // In the fix and revert flows the flush comes only after the elevated helper reported success.
        foreach (var method in new[] { "private async Task FixAsync()", "private async Task RevertAsync()" })
        {
            var body = Body(vm, method);
            var elevate = body.IndexOf("_app.Elevation.RunAsync(", StringComparison.Ordinal);
            var guard = body.IndexOf("HandleElevationOutcome(", StringComparison.Ordinal);
            var flush = body.IndexOf("FlushDnsAsync()", StringComparison.Ordinal);
            Assert.True(elevate > 0 && guard > elevate && flush > guard, method);
        }
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = source.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "RiotFix.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>The main screen speaks plain language and uses the five agreed states (v0.2.1).</summary>
public class PlainLanguageTests
{
    private static DomainDiagnosis D(string host, DomainHealth health) => ClassificationTests.Domain(host, health);

    private static DiagnosisReport Report(Verdict verdict, FixPlan? plan = null, params DomainDiagnosis[] domains) =>
        new(DateTimeOffset.Now, TimeSpan.Zero, domains, false, true, false, [], plan ?? FixPlan.Empty, verdict);

    private static readonly HostPin Pin = new("auth.riotgames.com", System.Net.IPAddress.Parse("104.16.120.50"));

    private static FixPlan PlanWith(HostPin pin) => FixPlan.Empty with { NewPins = [pin], DesiredPins = [pin] };

    public static TheoryData<Verdict, string> Headlines => new()
    {
        { Verdict.AllHealthy, "Подключение работает" },
        { Verdict.LimitedConnectivity, "Подключение работает" },
        { Verdict.FixAvailable, "Обнаружена проблема с подключением" },
        { Verdict.ProblemsWithoutFix, "Не удалось найти безопасное исправление" },
        { Verdict.ServiceErrors, "Есть проблемы с подключением" },
        { Verdict.NoConnectivity, "Нет подключения к интернету" },
    };

    [Theory]
    [MemberData(nameof(Headlines))]
    public void Every_verdict_has_a_simple_headline_and_no_technical_details(Verdict verdict, string headline)
    {
        var report = Report(verdict, verdict == Verdict.FixAvailable ? PlanWith(Pin) : null,
            D("auth.riotgames.com", verdict is Verdict.AllHealthy or Verdict.LimitedConnectivity ? DomainHealth.Healthy : DomainHealth.Failed),
            D("account.riotgames.com", verdict == Verdict.AllHealthy ? DomainHealth.Healthy : DomainHealth.Failed));

        var message = Texts.ForReport(report);

        Assert.Equal(headline, message.Headline);
        Assert.Empty(PlainTextPolicy.FindTechnicalTerms(message.Headline + " " + message.Body));
        Assert.DoesNotContain("гарантир", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(message.Body.Length <= 160, $"Main-screen message too long ({message.Body.Length}): {message.Body}");
    }

    [Fact]
    public void Auxiliary_problem_alone_is_never_shown_as_a_red_error()
    {
        var message = Texts.ForReport(Report(Verdict.LimitedConnectivity, null,
            D("auth.riotgames.com", DomainHealth.Healthy), D("data.riotgames.com", DomainHealth.Failed)));
        Assert.NotEqual(StatusTone.Error, message.Tone);
    }

    [Theory]
    [InlineData(DomainHealth.Healthy, DomainHealth.Healthy)]
    [InlineData(DomainHealth.Healthy, DomainHealth.Failed)]
    [InlineData(DomainHealth.Failed, DomainHealth.Healthy)]
    [InlineData(DomainHealth.Failed, DomainHealth.Failed)]
    public void After_fix_always_says_changes_were_applied_in_plain_words(DomainHealth auth, DomainHealth config)
    {
        var after = Report(Verdict.AllHealthy, null, D("auth.riotgames.com", auth), D("clientconfig.rpg.riotgames.com", config));
        var message = Texts.AfterFix(after, [Pin]);
        Assert.Equal("Изменения применены", message.Headline);
        Assert.Empty(PlainTextPolicy.FindTechnicalTerms(message.Body));
    }

    [Theory]
    [InlineData("Подключение работает. Сервисы Riot Games отвечают.", 0)]
    [InlineData("104.16.120.50", 1)]
    [InlineData("auth.riotgames.com", 1)]
    [InlineData("ошибка TLS", 1)]
    [InlineData("нет ответа DNS", 1)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", 2)]
    [InlineData("Версия 0.2.1", 0)]
    [InlineData("Доступна новая версия 9.9.9", 0)]
    public void Policy_detects_technical_terms(string text, int expected)
    {
        Assert.Equal(expected, PlainTextPolicy.FindTechnicalTerms(text).Count);
    }

    [Fact]
    public void Technical_report_still_contains_everything()
    {
        var report = Report(Verdict.FixAvailable, PlanWith(Pin), D("auth.riotgames.com", DomainHealth.Failed));
        var text = ReportFormatter.Format(report, "0.2.1");
        Assert.Contains("RiotFix 0.2.1", text, StringComparison.Ordinal);
        Assert.Contains("auth.riotgames.com", text, StringComparison.Ordinal);
        Assert.Contains("+ 104.16.120.50 auth.riotgames.com", text, StringComparison.Ordinal);
        Assert.Contains("Системный DNS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_messages_contain_no_host_names()
    {
        var dns = new FakeSystemResolver();
        var cf = new FakeTrustedResolver("Cloudflare DNS").Set("auth.riotgames.com", "104.16.120.50");
        var prober = new FakeProber().Ok("auth.riotgames.com", "104.16.120.50");
        var messages = new List<string>();
        var engine = new DiagnosticsEngine(dns, [cf], prober, new FakeProxy(), HostsDocument.Empty, TestOptions.Fast);

        await engine.RunAsync([RiotServices.All[0]], new SyncProgress(p => { lock (messages) { messages.Add(p.Message); } }), CancellationToken.None);

        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Empty(PlainTextPolicy.FindTechnicalTerms(m)));
    }

    private sealed class SyncProgress(Action<DiagnosisProgress> report) : IProgress<DiagnosisProgress>
    {
        public void Report(DiagnosisProgress value) => report(value);
    }
}
