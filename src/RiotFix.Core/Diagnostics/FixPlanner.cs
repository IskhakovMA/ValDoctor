using RiotFix.Core.Hosts;

namespace RiotFix.Core.Diagnostics;

/// <summary>
/// Turns a diagnosis into a hosts plan. Rules:
/// <list type="bullet">
/// <item>Only hosts that are Degraded/Failed get a new pin, and only to a candidate verified twice end-to-end.</item>
/// <item>Existing RiotFix pins for hosts that are fine are kept; a broken RiotFix pin is replaced only by a verified candidate.</item>
/// <item>Third-party entries for a host to be pinned become conflicts; lines that also map unrelated names are never touched.</item>
/// <item>If nothing verified is found, the plan has no changes and the hosts file is not modified.</item>
/// </list>
/// </summary>
public static class FixPlanner
{
    public static FixPlan Create(IReadOnlyList<DomainDiagnosis> domains, HostsDocument? hosts)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (hosts is null)
        {
            return FixPlan.Empty with
            {
                Unfixable = domains.Where(d => d.NeedsFix).Select(d => new UnfixableDomain(d.Host, UnfixableReason.HostsUnreadable)).ToList(),
            };
        }

        var desired = new Dictionary<string, HostPin>(StringComparer.OrdinalIgnoreCase);
        var newPins = new List<HostPin>();
        var replaced = new List<HostPin>();
        var unfixable = new List<UnfixableDomain>();

        // Keep RiotFix pins for hosts outside this diagnosis untouched.
        foreach (var pin in hosts.GetPins())
        {
            desired[pin.Host] = pin;
        }

        foreach (var domain in domains)
        {
            var existing = hosts.GetPin(domain.Host);
            if (!domain.NeedsFix)
            {
                continue;
            }

            var best = domain.BestCandidate;
            if (best is null)
            {
                unfixable.Add(new UnfixableDomain(domain.Host, UnfixableReason.NoVerifiedCandidate));
                continue;
            }

            if (existing is not null && existing.Address.Equals(best.Address))
            {
                continue;
            }

            var pin = new HostPin(domain.Host, best.Address);
            desired[domain.Host] = pin;
            newPins.Add(pin);
            if (existing is not null)
            {
                replaced.Add(existing);
            }
        }

        // Conflicts: third-party entries for any host in the desired block (this is exactly what
        // HostsDocument.SetPins will check). Lines that also map unrelated names block that host.
        var conflicts = hosts.FindConflicts(desired.Keys);
        var blocked = conflicts.Where(c => !c.CanDisable).SelectMany(c => c.AffectedHosts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (blocked.Count > 0)
        {
            foreach (var host in blocked)
            {
                newPins.RemoveAll(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
                replaced.RemoveAll(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));
                desired.Remove(host);
                unfixable.Add(new UnfixableDomain(host, UnfixableReason.ConflictCannotBeDisabled));
            }

            conflicts = hosts.FindConflicts(desired.Keys);
        }

        return new FixPlan(
            desired.Values.OrderBy(p => RiotServices.OrderOf(p.Host)).ToList(),
            newPins,
            replaced,
            conflicts,
            unfixable);
    }
}
