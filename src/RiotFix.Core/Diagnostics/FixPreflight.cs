using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;
using RiotFix.Core.Network;

namespace RiotFix.Core.Diagnostics;

public enum PreflightStatus
{
    /// <summary>The plan is still valid: every new address answered again and the hosts file is unchanged.</summary>
    Ready,

    /// <summary>A fresh diagnosis produced a different plan; the user must review it before anything is written.</summary>
    PlanChanged,

    /// <summary>A fresh diagnosis found nothing to fix (any more). Nothing must be written.</summary>
    NoLongerNeeded,
}

public sealed record PreflightResult(PreflightStatus Status, DiagnosisReport Report, string Reason);

/// <summary>
/// Re-validates a diagnosis right before the hosts file is changed (and before UAC is requested), so a result
/// that is minutes old never writes an address that stopped working or overwrites hosts changes made since.
/// <list type="number">
/// <item>Too old, or the hosts file changed → run a fresh diagnosis.</item>
/// <item>Otherwise probe every new address once more (full TCP → TLS/SNI → HTTPS check); any failure → fresh diagnosis.</item>
/// <item>A fresh diagnosis with the same new pins is <see cref="PreflightStatus.Ready"/>; a different one is
/// <see cref="PreflightStatus.PlanChanged"/>; no changes is <see cref="PreflightStatus.NoLongerNeeded"/>.</item>
/// </list>
/// </summary>
public sealed class FixPreflight
{
    private readonly Func<CancellationToken, Task<DiagnosisReport>> _diagnose;
    private readonly IEndpointProber _prober;
    private readonly Func<HostsDocument?> _loadHosts;
    private readonly TimeProvider _time;
    private readonly IRiotFixLog _log;

    public FixPreflight(
        Func<CancellationToken, Task<DiagnosisReport>> diagnose,
        IEndpointProber prober,
        Func<HostsDocument?> loadHosts,
        TimeProvider? time = null,
        IRiotFixLog? log = null)
    {
        _diagnose = diagnose;
        _prober = prober;
        _loadHosts = loadHosts;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLog.Instance;
    }

    public static TimeSpan MaxAge { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan ProbeBudget { get; } = TimeSpan.FromSeconds(20);

    public async Task<PreflightResult> CheckAsync(DiagnosisReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var reason = await FindStalenessAsync(report, cancellationToken).ConfigureAwait(false);
        if (reason is null)
        {
            return new PreflightResult(PreflightStatus.Ready, report, "ok");
        }

        _log.Info($"Preflight: {reason}; running a fresh diagnosis.");
        var fresh = await _diagnose(cancellationToken).ConfigureAwait(false);
        if (!fresh.Plan.HasChanges)
        {
            return new PreflightResult(PreflightStatus.NoLongerNeeded, fresh, reason);
        }

        return SamePlan(report.Plan, fresh.Plan)
            ? new PreflightResult(PreflightStatus.Ready, fresh, reason)
            : new PreflightResult(PreflightStatus.PlanChanged, fresh, reason);
    }

    internal static bool SamePlan(FixPlan a, FixPlan b) =>
        Keys(a.DesiredPins).SetEquals(Keys(b.DesiredPins)) &&
        Keys(a.NewPins).SetEquals(Keys(b.NewPins)) &&
        a.ConflictsToDisable.Select(c => c.LineText).ToHashSet(StringComparer.Ordinal)
            .SetEquals(b.ConflictsToDisable.Select(c => c.LineText)) &&
        Keys(a.RemovedPins).SetEquals(Keys(b.RemovedPins));

    private static HashSet<string> Keys(IEnumerable<HostPin> pins) =>
        pins.Select(p => $"{p.Host.ToLowerInvariant()}|{p.Address}").ToHashSet(StringComparer.Ordinal);

    private async Task<string?> FindStalenessAsync(DiagnosisReport report, CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - report.StartedAt.ToUniversalTime() > MaxAge)
        {
            return "result is older than 5 minutes";
        }

        HostsDocument? hosts;
        try
        {
            hosts = _loadHosts();
        }
        catch (Exception ex) when (ex is HostsAccessException or IOException or UnauthorizedAccessException)
        {
            return "hosts file could not be read";
        }

        if (hosts is null || report.HostsFingerprint is null || HostsFingerprint.Of(hosts) != report.HostsFingerprint)
        {
            return "hosts file changed since the diagnosis";
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProbeBudget);
        try
        {
            var probes = await Task.WhenAll(report.Plan.NewPins.Select(pin => _prober.ProbeAsync(pin.Host, pin.Address, budget.Token)))
                .ConfigureAwait(false);
            var failed = probes.Zip(report.Plan.NewPins).Where(x => !x.First.Healthy).Select(x => x.Second).ToList();
            return failed.Count == 0
                ? null
                : $"address no longer answers: {string.Join(", ", failed.Select(p => $"{p.Host}={p.Address}"))}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "re-check timed out";
        }
    }
}
