using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Network;

namespace ValDoctor.Core.Diagnostics;

public sealed record DiagnosticsOptions
{
    /// <summary>The timings used by the real application.</summary>
    public static DiagnosticsOptions Product { get; } = new();

    public int MaxSystemIPv4 { get; init; } = 4;

    public int MaxSystemIPv6 { get; init; } = 2;

    /// <summary>Upper bound of alternative addresses tried per host. Keeps the search bounded — no brute force.</summary>
    public int MaxCandidatesPerHost { get; init; } = 6;

    public int MaxParallelProbes { get; init; } = 8;

    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Pauses before re-asking the system DNS after a failed lookup. A real log (26.10.2026) showed the system DNS
    /// not answering for all six hosts at once and working again seconds later; without retries ValDoctor proposed six
    /// unnecessary hosts entries. A persistent failure still fails after the retries and gets a verified fix.
    /// <para>
    /// The Windows DNS cache is deliberately NOT flushed during a check: the check must not change system state.
    /// A timeout is not cached, so a plain retry reaches the DNS server again; a cached negative answer
    /// ("name not found") is a persistent answer, which the verified fix handles.
    /// </para>
    /// </summary>
    public IReadOnlyList<TimeSpan> DnsRetryDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)];

    public TimeSpan TrustedDnsTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard upper bound for one probe, on top of the prober's own per-stage timeouts.</summary>
    public TimeSpan ProbeBudget { get; init; } = TimeSpan.FromSeconds(25);
}

/// <summary>
/// Runs the read-only diagnosis. Never modifies anything: it resolves names, opens TLS connections to
/// public Riot server addresses and reads the hosts file.
/// </summary>
public sealed class DiagnosticsEngine
{
    internal const string SystemDnsSource = "Системный DNS";

    private readonly ISystemResolver _system;
    private readonly IReadOnlyList<ITrustedResolver> _trusted;
    private readonly IEndpointProber _prober;
    private readonly IProxyDetector _proxy;
    private readonly Func<HostsDocument?> _loadHosts;
    private readonly DiagnosticsOptions _options;
    private readonly IAppLog _log;

    public DiagnosticsEngine(
        ISystemResolver system,
        IReadOnlyList<ITrustedResolver> trusted,
        IEndpointProber prober,
        IProxyDetector proxy,
        Func<HostsDocument?> loadHosts,
        DiagnosticsOptions? options = null,
        IAppLog? log = null)
    {
        _system = system;
        _trusted = trusted;
        _prober = prober;
        _proxy = proxy;
        _loadHosts = loadHosts;
        _options = options ?? new DiagnosticsOptions();
        _log = log ?? NullLog.Instance;
    }

    /// <summary>The options this engine runs with (exposed so tests can compare test and app configuration).</summary>
    public DiagnosticsOptions Options => _options;

    /// <summary>
    /// The engine exactly as the application uses it: product timings (including DNS retries), no test shortcuts.
    /// AppComposition must create engines only through this method.
    /// </summary>
    public static DiagnosticsEngine CreateDefault(
        ISystemResolver system,
        IReadOnlyList<ITrustedResolver> trusted,
        IEndpointProber prober,
        IProxyDetector proxy,
        Func<HostsDocument?> loadHosts,
        IAppLog log) =>
        new(system, trusted, prober, proxy, loadHosts, DiagnosticsOptions.Product, log);

    public async Task<DiagnosisReport> RunAsync(
        IReadOnlyList<RiotService> services,
        IProgress<DiagnosisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var started = DateTimeOffset.Now;
        var sw = Stopwatch.StartNew();
        _log.Info($"Diagnosis started for {services.Count} hosts.");

        HostsDocument? hosts = null;
        try
        {
            hosts = _loadHosts();
        }
        catch (Exception ex) when (ex is HostsAccessException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Hosts file could not be read: {ex.GetType().Name}");
        }

        var proxy = services.Count > 0 && _proxy.IsProxyConfigured(services[0].Host);
        using var throttle = new SemaphoreSlim(_options.MaxParallelProbes);
        var completed = 0;
        progress?.Report(new DiagnosisProgress(0, services.Count, "Проверяем подключение…"));

        var tasks = services.Select(async service =>
        {
            var result = await DiagnoseAsync(service, hosts, throttle, progress, services.Count, cancellationToken).ConfigureAwait(false);
            var done = Interlocked.Increment(ref completed);
            progress?.Report(new DiagnosisProgress(done, services.Count, $"Проверено {done} из {services.Count}"));
            return result;
        }).ToList();

        var domains = await Task.WhenAll(tasks).ConfigureAwait(false);
        var plan = FixPlanner.Create(domains, hosts);
        var verdict = DetermineVerdict(domains, plan);
        sw.Stop();

        _log.Info($"Diagnosis finished in {sw.ElapsedMilliseconds} ms: verdict {verdict}, plan +{plan.NewPins.Count} pins, {plan.Unfixable.Count} unfixable, proxy={proxy}.");
        return new DiagnosisReport(
            started,
            sw.Elapsed,
            domains,
            proxy,
            hosts is not null,
            hosts?.HasManagedContent ?? false,
            hosts?.GetPins() ?? [],
            plan,
            verdict,
            hosts is null ? null : HostsFingerprint.Of(hosts));
    }

    internal static Verdict DetermineVerdict(IReadOnlyList<DomainDiagnosis> domains, FixPlan plan)
    {
        if (domains.All(d => d.IsReachable))
        {
            return Verdict.AllHealthy;
        }

        if (plan.HasChanges)
        {
            return Verdict.FixAvailable;
        }

        var nothingConnected = domains.All(d => d.Health == DomainHealth.Failed && d.SystemProbes.All(p => p.Stage < ProbeStage.TlsEstablished));
        var noTrustedAnswered = domains.All(d => d.TrustedAnswers.All(a => !a.Responded));
        if (nothingConnected && noTrustedAnswered)
        {
            return Verdict.NoConnectivity;
        }

        var notReachable = domains.Where(d => !d.IsReachable).ToList();
        if (notReachable.All(d => d.Health == DomainHealth.ServerError))
        {
            return Verdict.ServiceErrors;
        }

        return notReachable.All(d => !d.IsEssential) ? Verdict.LimitedConnectivity : Verdict.ProblemsWithoutFix;
    }

    internal static DomainHealth Classify(SystemDnsAnswer dns, IReadOnlyList<ProbeResult> probes)
    {
        if (!dns.Success || probes.Count == 0)
        {
            return DomainHealth.Failed;
        }

        // The order of addresses is not stable (DNS round-robin), so every address counts, not just the first.
        var anyHealthy = probes.Any(p => p.Healthy);

        // "No IPv6 route" while another address works is harmless: the OS uses IPv4 immediately.
        var relevant = probes.Where(p => !(anyHealthy &&
            p.Address.AddressFamily == AddressFamily.InterNetworkV6 && p.Failure == ProbeFailure.NetworkUnreachable)).ToList();
        var failing = relevant.Where(p => !p.Healthy).ToList();
        if (failing.Count == 0)
        {
            return DomainHealth.Healthy;
        }

        if (anyHealthy)
        {
            // Addresses that fail instantly at connect time are skipped by the client right away.
            return failing.All(p => IsFastConnectFailure(p.Failure)) ? DomainHealth.PartiallyReachable : DomainHealth.Degraded;
        }

        return relevant.Any(p => p.Reachable) ? DomainHealth.ServerError : DomainHealth.Failed;
    }

    /// <summary>
    /// Failures the client gets immediately, so it moves on to the next address. A non-public DNS answer is NOT one:
    /// ValDoctor never connects to it, it may black-hole or fail TLS for a long time, and it usually means DNS tampering.
    /// </summary>
    internal static bool IsFastConnectFailure(ProbeFailure failure) =>
        failure is ProbeFailure.TcpRefused or ProbeFailure.TcpReset or ProbeFailure.NetworkUnreachable;

    private async Task<DomainDiagnosis> DiagnoseAsync(
        RiotService service,
        HostsDocument? hosts,
        SemaphoreSlim throttle,
        IProgress<DiagnosisProgress>? progress,
        int total,
        CancellationToken cancellationToken)
    {
        var host = service.Host;
        var hostsOverride = hosts is null ? HostsOverride.None
            : hosts.GetPin(host) is not null ? HostsOverride.ManagedPin
            : hosts.GetForeignEntries(host).Count > 0 ? HostsOverride.ForeignEntry
            : HostsOverride.None;

        var dns = await ResolveSystemAsync(host, cancellationToken).ConfigureAwait(false);
        var retried = false;
        for (var i = 0; !dns.Success && i < _options.DnsRetryDelays.Count; i++)
        {
            retried = true;
            await Task.Delay(_options.DnsRetryDelays[i], cancellationToken).ConfigureAwait(false);
            dns = await ResolveSystemAsync(host, cancellationToken).ConfigureAwait(false);
            _log.Info($"{host}: system DNS retry {i + 1}: {dns.Failure}.");
        }

        var addresses = SelectSystemAddresses(dns.Addresses);
        var probes = await Task.WhenAll(addresses.Select(a => ProbeAsync(host, a, throttle, cancellationToken))).ConfigureAwait(false);
        var health = Classify(dns, probes);
        _log.Info($"{host}: dns={dns.Failure} [{string.Join(", ", dns.Addresses)}] override={hostsOverride} health={health} " +
                  $"probes=[{string.Join("; ", probes.Select(Describe))}]");

        IReadOnlyList<TrustedDnsAnswer> trusted = [];
        IReadOnlyList<CandidateEvidence> candidates = [];
        if (health is DomainHealth.Degraded or DomainHealth.Failed)
        {
            progress?.Report(new DiagnosisProgress(0, total, "Ищем рабочий вариант подключения…"));
            trusted = await QueryTrustedAsync(host, cancellationToken).ConfigureAwait(false);
            candidates = await VerifyCandidatesAsync(host, hostsOverride, probes, trusted, throttle, cancellationToken).ConfigureAwait(false);
            _log.Info($"{host}: trusted=[{string.Join("; ", trusted.Select(t => $"{t.ResolverName}:{(t.Responded ? string.Join(",", t.Addresses) : "no answer " + t.Error)}"))}] " +
                      $"candidates=[{string.Join("; ", candidates.Select(c => $"{c.Address} verified={c.Verified} src={c.Sources.Count}"))}]");
        }

        return new DomainDiagnosis(service, dns, probes, health, hostsOverride, trusted, candidates, retried);
    }

    private async Task<SystemDnsAnswer> ResolveSystemAsync(string host, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.DnsTimeout);
        try
        {
            return await _system.ResolveAsync(host, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SystemDnsAnswer([], DnsFailureKind.Timeout, "timeout");
        }
    }

    private List<IPAddress> SelectSystemAddresses(IReadOnlyList<IPAddress> addresses)
    {
        var v4 = 0;
        var v6 = 0;
        var result = new List<IPAddress>();
        foreach (var address in addresses.Distinct())
        {
            if (address.AddressFamily == AddressFamily.InterNetwork && v4 < _options.MaxSystemIPv4)
            {
                v4++;
                result.Add(address);
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && v6 < _options.MaxSystemIPv6)
            {
                v6++;
                result.Add(address);
            }
        }

        return result;
    }

    private async Task<ProbeResult> ProbeAsync(string host, IPAddress address, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        if (!AddressPolicy.IsPublicUnicast(address))
        {
            // Never connect to loopback / LAN / reserved addresses. Such answers usually mean DNS-level blocking.
            return new ProbeResult(address, ProbeStage.None, ProbeFailure.NotPublicAddress, TimeSpan.Zero);
        }

        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.ProbeBudget);
            // No WaitAsync here: the semaphore must be held until the probe really finishes. The prober honours the token.
            return await _prober.ProbeAsync(host, address, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(address, ProbeStage.None, ProbeFailure.Timeout, sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"{host} {address}: probe error {ex.GetType().Name}");
            return new ProbeResult(address, ProbeStage.None, ProbeFailure.Error, sw.Elapsed, Detail: ex.GetType().Name);
        }
        finally
        {
            throttle.Release();
        }
    }

    private async Task<IReadOnlyList<TrustedDnsAnswer>> QueryTrustedAsync(string host, CancellationToken cancellationToken)
    {
        var tasks = _trusted.Select(async resolver =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.TrustedDnsTimeout);
            try
            {
                return await resolver.ResolveAsync(host, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new TrustedDnsAnswer(resolver.Name, false, [], "timeout");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new TrustedDnsAnswer(resolver.Name, false, [], ex.GetType().Name);
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CandidateEvidence>> VerifyCandidatesAsync(
        string host,
        HostsOverride hostsOverride,
        IReadOnlyList<ProbeResult> systemProbes,
        IReadOnlyList<TrustedDnsAnswer> trusted,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        var sources = new Dictionary<IPAddress, List<string>>();
        var order = new List<IPAddress>();

        void AddSource(IPAddress address, string source)
        {
            if (!AddressPolicy.IsPublicUnicast(address))
            {
                return;
            }

            if (!sources.TryGetValue(address, out var list))
            {
                sources[address] = list = [];
                order.Add(address);
            }

            if (!list.Contains(source))
            {
                list.Add(source);
            }
        }

        // System DNS answers only count as evidence when they did not come from a hosts override.
        var systemHealthy = new Dictionary<IPAddress, ProbeResult>();
        if (hostsOverride == HostsOverride.None)
        {
            foreach (var probe in systemProbes.Where(p => p.Healthy))
            {
                systemHealthy[probe.Address] = probe;
                AddSource(probe.Address, SystemDnsSource);
            }
        }

        foreach (var answer in trusted.Where(a => a.Responded))
        {
            foreach (var address in answer.Addresses)
            {
                AddSource(address, answer.ResolverName);
            }
        }

        // Addresses that already failed end-to-end in this run are not retried.
        var failed = systemProbes.Where(p => !p.Healthy).Select(p => p.Address).ToHashSet();
        var selected = order
            .Where(a => !failed.Contains(a))
            .Select((a, index) => (Address: a, Index: index))
            .OrderBy(x => x.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenByDescending(x => sources[x.Address].Count)
            .ThenBy(x => x.Index)
            .Take(_options.MaxCandidatesPerHost)
            .Select(x => x.Address)
            .ToList();

        var tasks = selected.Select(async address =>
        {
            var probes = new List<ProbeResult>();
            if (systemHealthy.TryGetValue(address, out var earlier))
            {
                probes.Add(earlier);
            }
            else
            {
                probes.Add(await ProbeAsync(host, address, throttle, cancellationToken).ConfigureAwait(false));
            }

            if (probes[0].Healthy)
            {
                probes.Add(await ProbeAsync(host, address, throttle, cancellationToken).ConfigureAwait(false));
            }

            return new CandidateEvidence(address, sources[address], probes);
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string Describe(ProbeResult p) =>
        p.Reachable
            ? $"{p.Address} HTTP {p.HttpStatus} {p.Elapsed.TotalMilliseconds:0}ms"
            : $"{p.Address} {p.Failure} at {p.Stage}{(p.Detail is null ? string.Empty : " (" + p.Detail + ")")}";
}
