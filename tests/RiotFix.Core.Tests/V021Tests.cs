using RiotFix.Core.Diagnostics;
using RiotFix.Core.Hosts;
using RiotFix.Core.Network;
using RiotFix.Core.Tests.Fakes;

namespace RiotFix.Core.Tests;

/// <summary>Short-lived DNS outages must not lead to hosts changes (real log, 26.10.2026 14:28).</summary>
public class TransientDnsTests
{
    private readonly FakeSystemResolver _dns = new();
    private readonly FakeTrustedResolver _cloudflare = new("Cloudflare DNS");
    private readonly FakeProber _prober = new();
    private int _flushes;

    private DiagnosticsEngine Engine() => new(_dns, [_cloudflare], _prober, new FakeProxy(), HostsDocument.Empty,
        new DiagnosticsOptions { DnsRetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)] },
        flushDnsCache: () => Interlocked.Increment(ref _flushes));

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
        Assert.True(_flushes >= 1);
        Assert.All(report.Domains, d => Assert.True(d.DnsRetried));
    }

    [Fact]
    public async Task Persistent_dns_failure_still_gets_a_verified_fix()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 100);

        var report = await Engine().RunAsync(RiotServices.All, null, CancellationToken.None);

        Assert.Equal(Verdict.FixAvailable, report.Verdict);
        Assert.Equal("auth.riotgames.com", report.Plan.NewPins.Single().Host);
        Assert.Equal(3, _dns.CallsTo("auth.riotgames.com")); // first try + 2 retries, no more
    }

    [Fact]
    public async Task Dns_answer_that_works_is_never_retried()
    {
        AllSixWork();
        await Engine().RunAsync(RiotServices.All, null, CancellationToken.None);
        Assert.All(RiotServices.All, s => Assert.Equal(1, _dns.CallsTo(s.Host)));
        Assert.Equal(0, _flushes);
    }

    [Fact]
    public async Task Plan_built_on_a_dns_failure_is_always_rechecked_before_uac()
    {
        AllSixWork();
        _dns.FailFirst("auth.riotgames.com", 3);
        var engine = Engine();
        var report = await engine.RunAsync(RiotServices.All, null, CancellationToken.None);
        Assert.True(report.Plan.HasChanges);

        // Moments later the system DNS works again: the preflight must notice it and drop the fix.
        var diagnoses = 0;
        var preflight = new FixPreflight(ct => { diagnoses++; return engine.RunAsync(RiotServices.All, null, ct); }, _prober, HostsDocument.Empty);
        var result = await preflight.CheckAsync(report, CancellationToken.None);

        Assert.Equal(1, diagnoses);
        Assert.Equal(PreflightStatus.NoLongerNeeded, result.Status);
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
