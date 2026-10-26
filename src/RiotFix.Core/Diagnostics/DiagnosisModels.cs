using System.Net;
using System.Net.Sockets;
using RiotFix.Core.Hosts;
using RiotFix.Core.Network;

namespace RiotFix.Core.Diagnostics;

public enum DomainHealth
{
    /// <summary>Every relevant resolved address works end-to-end (DNS → TCP → TLS → HTTP status &lt; 500).</summary>
    Healthy,

    /// <summary>
    /// Some addresses work; the others fail immediately at connect time (refused / no route / reset).
    /// Clients move on to the next address at once, so this is not treated as a problem.
    /// </summary>
    PartiallyReachable,

    /// <summary>
    /// Some addresses work, others fail slowly (TCP timeout) or after connecting (TLS/HTTPS). DNS rotates the
    /// address order, so a client regularly lands on a bad address. A verified pin removes that.
    /// </summary>
    Degraded,

    /// <summary>The server is reachable over validated TLS but answers with HTTP 5xx. Not a local network problem.</summary>
    ServerError,

    /// <summary>No resolved address works (or the name does not resolve).</summary>
    Failed,
}

public enum HostsOverride
{
    None,
    RiotFixPin,
    ForeignEntry,
}

/// <summary>An alternative address for a host, with where it came from and the probes that verified it.</summary>
public sealed record CandidateEvidence(IPAddress Address, IReadOnlyList<string> Sources, IReadOnlyList<ProbeResult> Probes)
{
    public const int RequiredConfirmations = 2;

    /// <summary>Two consecutive healthy end-to-end probes with a valid certificate for the host.</summary>
    public bool Verified => Probes.Count >= RequiredConfirmations && Probes.All(p => p.Healthy);

    public TimeSpan AverageLatency => Probes.Count == 0 ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)Probes.Average(p => p.Elapsed.Ticks));
}

public sealed record DomainDiagnosis(
    RiotService Service,
    SystemDnsAnswer SystemDns,
    IReadOnlyList<ProbeResult> SystemProbes,
    DomainHealth Health,
    HostsOverride Override,
    IReadOnlyList<TrustedDnsAnswer> TrustedAnswers,
    IReadOnlyList<CandidateEvidence> Candidates,
    bool DnsRetried = false)
{
    public string Host => Service.Host;

    public bool NeedsFix => Health is DomainHealth.Degraded or DomainHealth.Failed;

    /// <summary>Network-level OK: the client can reach the service (possibly after skipping a dead address).</summary>
    public bool IsReachable => Health is DomainHealth.Healthy or DomainHealth.PartiallyReachable;

    public bool IsEssential => Service.Role == ServiceRole.Essential;

    public CandidateEvidence? BestCandidate => Candidates
        .Where(c => c.Verified)
        .OrderBy(c => c.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
        .ThenByDescending(c => c.Sources.Count)
        .ThenBy(c => c.AverageLatency)
        .FirstOrDefault();

    /// <summary>
    /// Null when no trusted resolver answered. Otherwise whether any system-resolved address also appears in a
    /// trusted answer (a mismatch hints at DNS interference, or at a hosts override).
    /// </summary>
    public bool? SystemMatchesTrusted
    {
        get
        {
            var trusted = TrustedAnswers.Where(a => a.Responded).SelectMany(a => a.Addresses).ToHashSet();
            if (!TrustedAnswers.Any(a => a.Responded) || SystemDns.Addresses.Count == 0)
            {
                return null;
            }

            return SystemDns.Addresses.Any(trusted.Contains);
        }
    }
}

public enum UnfixableReason
{
    NoVerifiedCandidate,
    ConflictCannotBeDisabled,
    HostsUnreadable,
}

public sealed record UnfixableDomain(string Host, UnfixableReason Reason);

/// <summary>What RiotFix proposes to write. Built only from verified candidates.</summary>
public sealed record FixPlan(
    IReadOnlyList<HostPin> DesiredPins,
    IReadOnlyList<HostPin> NewPins,
    IReadOnlyList<HostPin> ReplacedPins,
    IReadOnlyList<HostPin> RemovedPins,
    IReadOnlyList<HostsConflict> ConflictsToDisable,
    IReadOnlyList<UnfixableDomain> Unfixable)
{
    public static FixPlan Empty { get; } = new([], [], [], [], [], []);

    public bool HasChanges => NewPins.Count > 0;

    public bool RequiresConflictConsent => ConflictsToDisable.Count > 0;
}

public enum Verdict
{
    AllHealthy,
    FixAvailable,
    ProblemsWithoutFix,
    ServiceErrors,
    NoConnectivity,

    /// <summary>All essential services are reachable; only auxiliary ones are not, and no verified fix exists.</summary>
    LimitedConnectivity,
}

public sealed record DiagnosisReport(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<DomainDiagnosis> Domains,
    bool ProxyConfigured,
    bool HostsReadable,
    bool HasRiotFixContent,
    IReadOnlyList<HostPin> CurrentPins,
    FixPlan Plan,
    Verdict Verdict,
    string? HostsFingerprint = null)
{
    public int HealthyCount => Domains.Count(d => d.IsReachable);

    public bool EssentialReachable => Domains.Where(d => d.IsEssential).All(d => d.IsReachable);
}

public sealed record DiagnosisProgress(int Completed, int Total, string Message);
