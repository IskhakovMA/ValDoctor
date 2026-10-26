using System.Net;
using System.Text;
using RiotFix.Core.Diagnostics;
using RiotFix.Core.Hosts;
using RiotFix.Core.Network;
using RiotFix.Core.Tests.Fakes;

namespace RiotFix.Core.Tests;

public class DiagnosticsEngineTests
{
    private const string Auth = "auth.riotgames.com";
    private const string Account = "account.riotgames.com";
    private static readonly RiotService AuthService = RiotServices.Find(Auth)!;
    private static readonly RiotService AccountService = RiotServices.Find(Account)!;

    private readonly FakeSystemResolver _dns = new();
    private readonly FakeTrustedResolver _cloudflare = new("Cloudflare DNS");
    private readonly FakeTrustedResolver _google = new("Google Public DNS");
    private readonly FakeProber _prober = new();
    private HostsDocument? _hosts = HostsDocument.Empty();
    private DiagnosticsOptions _options = new();

    private DiagnosticsEngine Engine() => new(_dns, [_cloudflare, _google], _prober, new FakeProxy(), () => _hosts, _options);

    private Task<DiagnosisReport> Run(params RiotService[] services) =>
        Engine().RunAsync(services.Length == 0 ? [AuthService, AccountService] : services, null, CancellationToken.None);

    [Fact]
    public async Task All_healthy_needs_no_fix_and_queries_no_trusted_dns()
    {
        _dns.Set(Auth, "104.16.1.1").Set(Account, "104.16.2.2");
        _prober.Ok(Auth, "104.16.1.1").Ok(Account, "104.16.2.2", status: 404);

        var report = await Run();

        Assert.Equal(Verdict.AllHealthy, report.Verdict);
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(0, _cloudflare.Calls);
    }

    [Fact]
    public async Task Http_401_and_403_count_as_network_reachable()
    {
        _dns.Set(Auth, "104.16.1.1").Set(Account, "104.16.2.2");
        _prober.Ok(Auth, "104.16.1.1", status: 401).Ok(Account, "104.16.2.2", status: 403);

        var report = await Run();
        Assert.Equal(Verdict.AllHealthy, report.Verdict);
    }

    [Fact]
    public async Task Blocked_address_is_replaced_by_verified_trusted_dns_candidate()
    {
        _dns.Set(Auth, "104.16.1.1").Set(Account, "104.16.2.2");
        _prober.Fails(Auth, "104.16.1.1", ProbeFailure.TcpTimeout).Ok(Account, "104.16.2.2");
        _cloudflare.Set(Auth, "104.16.1.1", "104.18.3.3");
        _google.Set(Auth, "104.18.3.3");
        _prober.Ok(Auth, "104.18.3.3");

        var report = await Run();

        Assert.Equal(Verdict.FixAvailable, report.Verdict);
        var pin = Assert.Single(report.Plan.NewPins);
        Assert.Equal(new HostPin(Auth, IPAddress.Parse("104.18.3.3")), pin);
        var candidate = report.Domains[0].BestCandidate!;
        Assert.True(candidate.Verified);
        Assert.Equal(2, candidate.Sources.Count);
        // The already failing address is not retried.
        Assert.Equal(1, _prober.CallsTo(Auth, "104.16.1.1"));
        // A verified candidate was probed twice.
        Assert.Equal(2, _prober.CallsTo(Auth, "104.18.3.3"));
    }

    [Fact]
    public async Task Partially_working_services_only_fix_broken_hosts()
    {
        _dns.Set(Auth, "104.16.1.1").Set(Account, "104.16.2.2");
        _prober.Ok(Auth, "104.16.1.1").Fails(Account, "104.16.2.2", ProbeFailure.TlsReset, ProbeStage.TcpConnected);
        _cloudflare.Set(Account, "104.18.4.4");
        _prober.Ok(Account, "104.18.4.4");

        var report = await Run();

        Assert.Equal(DomainHealth.Healthy, report.Domains[0].Health);
        Assert.Equal(DomainHealth.Failed, report.Domains[1].Health);
        Assert.Equal([new HostPin(Account, IPAddress.Parse("104.18.4.4"))], report.Plan.NewPins);
    }

    [Fact]
    public async Task Degraded_host_is_pinned_to_its_working_system_address()
    {
        // A TCP timeout is a slow failure: clients that land on this address hang before falling back.
        _dns.Set(Auth, "104.16.1.1", "104.16.9.9");
        _prober.Fails(Auth, "104.16.1.1", ProbeFailure.TcpTimeout).Ok(Auth, "104.16.9.9");

        var report = await Run(AuthService);

        Assert.Equal(DomainHealth.Degraded, report.Domains[0].Health);
        var pin = Assert.Single(report.Plan.NewPins);
        Assert.Equal("104.16.9.9", pin.Address.ToString());
        Assert.Contains(DiagnosticsEngine.SystemDnsSource, report.Domains[0].BestCandidate!.Sources);
    }

    [Fact]
    public async Task Invalid_certificates_are_never_accepted_and_hosts_is_not_changed()
    {
        _dns.Set(Auth, "104.16.1.1");
        _prober.BadCertificate(Auth, "104.16.1.1");
        _cloudflare.Set(Auth, "104.18.5.5");
        _prober.BadCertificate(Auth, "104.18.5.5");

        var report = await Run(AuthService);

        Assert.Equal(Verdict.ProblemsWithoutFix, report.Verdict);
        Assert.False(report.Plan.HasChanges);
        Assert.Empty(report.Plan.DesiredPins);
        Assert.Equal(UnfixableReason.NoVerifiedCandidate, Assert.Single(report.Plan.Unfixable).Reason);
        Assert.False(report.Domains[0].Candidates[0].Verified);
    }

    [Fact]
    public async Task Unstable_candidate_is_not_verified()
    {
        _dns.Fail(Auth, DnsFailureKind.NotFound);
        _cloudflare.Set(Auth, "104.18.6.6");
        _prober.Flaky(Auth, "104.18.6.6");

        var report = await Run(AuthService);
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(2, report.Domains[0].Candidates[0].Probes.Count);
    }

    [Fact]
    public async Task Prefers_ipv4_more_sources_and_lower_latency()
    {
        _dns.Fail(Auth, DnsFailureKind.NotFound);
        _cloudflare.Set(Auth, "2606:4700::1", "104.18.7.1", "104.18.7.2", "104.18.7.3");
        _google.Set(Auth, "104.18.7.3", "104.18.7.2");
        _prober.Ok(Auth, "2606:4700::1", ms: 5).Ok(Auth, "104.18.7.1", ms: 10).Ok(Auth, "104.18.7.2", ms: 90).Ok(Auth, "104.18.7.3", ms: 30);

        var report = await Run(AuthService);

        Assert.Equal("104.18.7.3", report.Plan.NewPins.Single().Address.ToString());
    }

    [Fact]
    public async Task Candidate_search_is_bounded()
    {
        _options = new DiagnosticsOptions { MaxCandidatesPerHost = 3 };
        _dns.Fail(Auth, DnsFailureKind.NotFound);
        _cloudflare.Set(Auth, Enumerable.Range(1, 20).Select(i => $"104.18.8.{i}").ToArray());

        var report = await Run(AuthService);

        Assert.Equal(3, report.Domains[0].Candidates.Count);
        Assert.Equal(3, _prober.Probed.Count);
    }

    [Fact]
    public async Task Non_public_addresses_are_never_probed_or_used()
    {
        _dns.Set(Auth, "0.0.0.0");
        _cloudflare.Set(Auth, "127.0.0.1", "10.0.0.1", "104.18.9.9");
        _prober.Ok(Auth, "104.18.9.9");

        var report = await Run(AuthService);

        Assert.Equal(ProbeFailure.NotPublicAddress, report.Domains[0].SystemProbes[0].Failure);
        Assert.DoesNotContain(_prober.Probed, k => k.Contains("|0.0.0.0", StringComparison.Ordinal) || k.Contains("|127.", StringComparison.Ordinal) || k.Contains("|10.", StringComparison.Ordinal));
        Assert.Equal("104.18.9.9", report.Plan.NewPins.Single().Address.ToString());
    }

    [Fact]
    public async Task Server_errors_are_not_treated_as_fixable()
    {
        _dns.Set(Auth, "104.16.1.1");
        _prober.Ok(Auth, "104.16.1.1", status: 503);

        var report = await Run(AuthService);

        Assert.Equal(DomainHealth.ServerError, report.Domains[0].Health);
        Assert.Equal(Verdict.ServiceErrors, report.Verdict);
        Assert.Equal(0, _cloudflare.Calls);
    }

    [Fact]
    public async Task No_connectivity_is_detected()
    {
        _dns.Fail(Auth, DnsFailureKind.Timeout).Fail(Account, DnsFailureKind.Timeout);
        _cloudflare.Unreachable = true;
        _google.Unreachable = true;

        var report = await Run();

        Assert.Equal(Verdict.NoConnectivity, report.Verdict);
        Assert.False(report.Plan.HasChanges);
    }

    [Fact]
    public async Task Hanging_probe_times_out_within_budget()
    {
        _options = new DiagnosticsOptions { ProbeBudget = TimeSpan.FromMilliseconds(200) };
        _dns.Set(Auth, "104.16.1.1");
        _prober.Hangs(Auth, "104.16.1.1");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await Run(AuthService);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(ProbeFailure.Timeout, report.Domains[0].SystemProbes[0].Failure);
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        _dns.Set(Auth, "104.16.1.1");
        _prober.Hangs(Auth, "104.16.1.1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().RunAsync([AuthService], null, cts.Token));
    }

    [Fact]
    public async Task Ipv6_without_route_does_not_count_as_a_problem()
    {
        _dns.Set(Auth, "2606:4700::1", "104.16.1.1");
        _prober.Fails(Auth, "2606:4700::1", ProbeFailure.NetworkUnreachable).Ok(Auth, "104.16.1.1");

        var report = await Run(AuthService);
        Assert.Equal(DomainHealth.Healthy, report.Domains[0].Health);
    }

    [Fact]
    public async Task Healthy_riotfix_pin_is_kept()
    {
        _hosts = HostsDocument.Empty();
        _hosts.SetPins([new HostPin(Auth, IPAddress.Parse("104.18.3.3"))], false);
        _dns.Set(Auth, "104.18.3.3");
        _prober.Ok(Auth, "104.18.3.3");

        var report = await Run(AuthService);

        Assert.Equal(HostsOverride.RiotFixPin, report.Domains[0].Override);
        Assert.False(report.Plan.HasChanges);
        Assert.Single(report.Plan.DesiredPins);
    }

    [Fact]
    public async Task Broken_riotfix_pin_is_replaced_by_verified_candidate()
    {
        _hosts = HostsDocument.Empty();
        _hosts.SetPins([new HostPin(Auth, IPAddress.Parse("104.18.3.3"))], false);
        _dns.Set(Auth, "104.18.3.3");
        _prober.Fails(Auth, "104.18.3.3", ProbeFailure.TcpTimeout);
        _cloudflare.Set(Auth, "104.18.3.3", "104.18.4.4");
        _prober.Ok(Auth, "104.18.4.4");

        var report = await Run(AuthService);

        Assert.Equal("104.18.4.4", report.Plan.NewPins.Single().Address.ToString());
        Assert.Equal("104.18.3.3", report.Plan.ReplacedPins.Single().Address.ToString());
    }

    [Fact]
    public async Task Broken_foreign_entry_requires_consent()
    {
        _hosts = HostsDocument.Parse(Encoding.ASCII.GetBytes("104.16.206.131 auth.riotgames.com\r\n"));
        _dns.Set(Auth, "104.16.206.131");
        _prober.Fails(Auth, "104.16.206.131", ProbeFailure.TcpTimeout);
        _cloudflare.Set(Auth, "104.18.4.4");
        _prober.Ok(Auth, "104.18.4.4");

        var report = await Run(AuthService);

        Assert.Equal(HostsOverride.ForeignEntry, report.Domains[0].Override);
        Assert.True(report.Plan.RequiresConflictConsent);
        Assert.Single(report.Plan.ConflictsToDisable);
        // System answers that come from a hosts override are not used as evidence.
        Assert.DoesNotContain(DiagnosticsEngine.SystemDnsSource, report.Domains[0].BestCandidate!.Sources);
    }

    [Fact]
    public async Task Mixed_foreign_line_makes_host_unfixable()
    {
        _hosts = HostsDocument.Parse(Encoding.ASCII.GetBytes("104.16.206.131 auth.riotgames.com mysite.local\r\n"));
        _dns.Set(Auth, "104.16.206.131");
        _prober.Fails(Auth, "104.16.206.131", ProbeFailure.TcpTimeout);
        _cloudflare.Set(Auth, "104.18.4.4");
        _prober.Ok(Auth, "104.18.4.4");

        var report = await Run(AuthService);

        Assert.False(report.Plan.HasChanges);
        Assert.Equal(UnfixableReason.ConflictCannotBeDisabled, report.Plan.Unfixable.Single().Reason);
    }

    [Fact]
    public async Task Conflict_resolution_is_stable_and_never_proposes_undisableable_lines()
    {
        // Dropping auth (mixed line) makes line 2 non-disableable for account as well.
        _hosts = HostsDocument.Parse(Encoding.ASCII.GetBytes(
            "1.2.3.4 auth.riotgames.com example.com\r\n5.6.7.8 auth.riotgames.com account.riotgames.com\r\n"));
        _dns.Set(Auth, "1.2.3.4").Set(Account, "5.6.7.8");
        _cloudflare.Set(Auth, "104.18.4.4").Set(Account, "104.18.5.5");
        _prober.Ok(Auth, "104.18.4.4").Ok(Account, "104.18.5.5");

        var report = await Run();

        Assert.All(report.Plan.ConflictsToDisable, c => Assert.True(c.CanDisable));
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(2, report.Plan.Unfixable.Count);
    }

    [Fact]
    public async Task Unreadable_hosts_file_means_no_fix()
    {
        _hosts = null;
        _dns.Set(Auth, "104.16.1.1");
        _cloudflare.Set(Auth, "104.18.4.4");
        _prober.Ok(Auth, "104.18.4.4");

        var report = await Run(AuthService);

        Assert.False(report.HostsReadable);
        Assert.False(report.Plan.HasChanges);
        Assert.Equal(UnfixableReason.HostsUnreadable, report.Plan.Unfixable.Single().Reason);
    }

    [Fact]
    public async Task Hosts_read_errors_are_handled()
    {
        var engine = new DiagnosticsEngine(_dns, [_cloudflare], _prober, new FakeProxy(),
            () => throw new HostsAccessException(HostsErrorKind.AccessDenied, "denied"));
        _dns.Set(Auth, "104.16.1.1");
        _prober.Ok(Auth, "104.16.1.1");

        var report = await engine.RunAsync([AuthService], null, CancellationToken.None);
        Assert.False(report.HostsReadable);
        Assert.Equal(Verdict.AllHealthy, report.Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("104.16.206.131 auth.riotgames.com\r\n")]
    [InlineData("104.16.206.131 auth.riotgames.com mysite.local\r\n")]
    [InlineData("104.16.206.131 auth.riotgames.com\r\n104.16.206.132 account.riotgames.com\r\n")]
    [InlineData("1.2.3.4 account.riotgames.com example.org\r\n")]
    public async Task Plan_always_applies_cleanly_to_the_hosts_it_was_built_from(string content)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        _hosts = HostsDocument.Parse(bytes);
        try
        {
            // Start from a state where RiotFix already pinned one host (when the file allows it).
            _hosts.SetPins([new HostPin(Account, IPAddress.Parse("104.18.9.1"))], disableConflicts: true);
        }
        catch (HostsConflictException)
        {
        }

        var start = _hosts.ToBytes();
        _hosts = HostsDocument.Parse(start);

        _dns.Set(Auth, "104.16.206.131").Set(Account, "104.18.9.1");
        _prober.Fails(Auth, "104.16.206.131", ProbeFailure.TcpTimeout).Fails(Account, "104.18.9.1", ProbeFailure.TcpTimeout);
        _cloudflare.Set(Auth, "104.18.4.4").Set(Account, "104.18.5.5");
        _prober.Ok(Auth, "104.18.4.4").Ok(Account, "104.18.5.5");

        var report = await Run();

        // What the elevated helper does with the plan must never hit an unexpected conflict.
        var target = HostsDocument.Parse(start);
        target.SetPins(report.Plan.DesiredPins, report.Plan.RequiresConflictConsent);
        Assert.True(report.Plan.HasChanges);
        foreach (var pin in report.Plan.NewPins)
        {
            Assert.Contains(pin, target.GetPins());
        }

        // Rolling back afterwards restores the original bytes.
        var reverted = HostsDocument.Parse(target.ToBytes());
        reverted.RevertAll();
        Assert.Equal(bytes, reverted.ToBytes());
    }

    [Fact]
    public async Task Report_text_lists_every_host()
    {
        _dns.Set(Auth, "104.16.1.1").Fail(Account, DnsFailureKind.NotFound);
        _prober.Ok(Auth, "104.16.1.1");
        _cloudflare.Set(Account, "104.18.4.4");
        _prober.Ok(Account, "104.18.4.4");

        var report = await Run();
        var text = ReportFormatter.Format(report, "0.1.0");

        Assert.Contains(Auth, text, StringComparison.Ordinal);
        Assert.Contains(Account, text, StringComparison.Ordinal);
        Assert.Contains("+ 104.18.4.4 account.riotgames.com", text, StringComparison.Ordinal);
    }
}
