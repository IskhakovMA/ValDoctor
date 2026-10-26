using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;
using RiotFix.Core.Network;

namespace RiotFix.Core.Elevation;

public enum ElevatedOperation
{
    Apply,
    Revert,
}

public sealed record ElevatedRequest(ElevatedOperation Operation, IReadOnlyList<HostPin> Pins, bool DisableConflicts)
{
    public static ElevatedRequest Apply(IReadOnlyList<HostPin> pins, bool disableConflicts) => new(ElevatedOperation.Apply, pins, disableConflicts);

    public static ElevatedRequest Revert() => new(ElevatedOperation.Revert, [], false);
}

public enum ElevatedExitCode
{
    Success = 0,
    NoChanges = 1,
    InvalidArguments = 2,
    AccessDenied = 3,
    ReadOnly = 4,
    Locked = 5,
    ConcurrentModification = 6,
    Conflict = 7,
    VerificationFailed = 8,
    UnsupportedFormat = 9,
    IoError = 10,
    UnexpectedError = 11,
}

/// <summary>
/// Command line contract between the normal (non-elevated) app and its short-lived elevated instance.
/// The elevated side trusts nothing: every host must be on the allowlist and every address public unicast,
/// so even a crafted command line can only write RiotFix's own block for Riot hosts.
/// Arguments are passed on the command line (not via a file) so there is nothing a non-admin process can swap.
/// </summary>
public static class ElevatedProtocol
{
    public const string Flag = "--riotfix-elevated";
    private const string PinOption = "--pin";
    private const string DisableConflictsOption = "--disable-conflicts";

    public static bool IsElevatedInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Flag;

    public static IReadOnlyList<string> ToArguments(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = new List<string> { Flag, request.Operation == ElevatedOperation.Apply ? "apply" : "revert" };
        if (request.Operation == ElevatedOperation.Apply)
        {
            foreach (var pin in request.Pins)
            {
                args.Add(PinOption);
                args.Add($"{pin.Host}={pin.Address}");
            }

            if (request.DisableConflicts)
            {
                args.Add(DisableConflictsOption);
            }
        }

        return args;
    }

    /// <exception cref="ArgumentException">The arguments are malformed or violate the allowlist / address policy.</exception>
    public static ElevatedRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count < 2 || args[0] != Flag)
        {
            throw new ArgumentException("Not an elevated RiotFix invocation.", nameof(args));
        }

        switch (args[1])
        {
            case "revert":
                if (args.Count != 2)
                {
                    throw new ArgumentException("'revert' takes no options.", nameof(args));
                }

                return ElevatedRequest.Revert();

            case "apply":
                var pins = new List<HostPin>();
                var disable = false;
                for (var i = 2; i < args.Count; i++)
                {
                    if (args[i] == DisableConflictsOption && !disable)
                    {
                        disable = true;
                    }
                    else if (args[i] == PinOption && i + 1 < args.Count)
                    {
                        pins.Add(ParsePin(args[++i]));
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument '{args[i]}'.", nameof(args));
                    }
                }

                if (pins.Count > RiotServices.All.Count ||
                    pins.Select(p => p.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Count)
                {
                    throw new ArgumentException("Duplicate or too many pins.", nameof(args));
                }

                return ElevatedRequest.Apply(pins, disable);

            default:
                throw new ArgumentException($"Unknown operation '{args[1]}'.", nameof(args));
        }
    }

    private static HostPin ParsePin(string value)
    {
        var eq = value.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0 || eq != value.LastIndexOf('='))
        {
            throw new ArgumentException($"Malformed pin '{value}'.", nameof(value));
        }

        var host = value[..eq].Trim().ToLowerInvariant();
        var ipText = value[(eq + 1)..].Trim();
        if (!RiotServices.IsAllowed(host))
        {
            throw new ArgumentException($"Host '{host}' is not allowed.", nameof(value));
        }

        if (!AddressPolicy.TryParseStrict(ipText, out var address) || !AddressPolicy.IsPublicUnicast(address))
        {
            throw new ArgumentException($"Address '{ipText}' is not an allowed public address.", nameof(value));
        }

        return new HostPin(host, address);
    }
}

/// <summary>The elevated side: performs exactly one hosts operation and reports the result as an exit code.</summary>
public static class ElevatedRunner
{
    public static ElevatedExitCode Run(IReadOnlyList<string> args, HostsFileService hosts, Action flushDns, IRiotFixLog log)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(flushDns);
        ArgumentNullException.ThrowIfNull(log);

        ElevatedRequest request;
        try
        {
            request = ElevatedProtocol.Parse(args);
        }
        catch (ArgumentException ex)
        {
            log.Warn($"Elevated: rejected arguments ({ex.Message}).");
            return ElevatedExitCode.InvalidArguments;
        }

        try
        {
            var result = request.Operation switch
            {
                ElevatedOperation.Apply => hosts.Update(doc => doc.SetPins(request.Pins, request.DisableConflicts)),
                _ => hosts.Update(doc => doc.RevertAll()),
            };

            if (!result.Written)
            {
                return ElevatedExitCode.NoChanges;
            }

            try
            {
                flushDns();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Warn($"Elevated: DNS cache flush failed ({ex.GetType().Name}).");
            }

            return ElevatedExitCode.Success;
        }
        catch (HostsConflictException)
        {
            return ElevatedExitCode.Conflict;
        }
        catch (HostsAccessException ex)
        {
            log.Error("Elevated: hosts update failed.", ex);
            return ex.Kind switch
            {
                HostsErrorKind.AccessDenied => ElevatedExitCode.AccessDenied,
                HostsErrorKind.ReadOnly => ElevatedExitCode.ReadOnly,
                HostsErrorKind.Locked => ElevatedExitCode.Locked,
                HostsErrorKind.ConcurrentModification => ElevatedExitCode.ConcurrentModification,
                HostsErrorKind.VerificationFailed => ElevatedExitCode.VerificationFailed,
                HostsErrorKind.UnsupportedFormat => ElevatedExitCode.UnsupportedFormat,
                _ => ElevatedExitCode.IoError,
            };
        }
        catch (ArgumentException ex)
        {
            log.Error("Elevated: invalid pins.", ex);
            return ElevatedExitCode.InvalidArguments;
        }
        catch (Exception ex)
        {
            log.Error("Elevated: unexpected error.", ex);
            return ElevatedExitCode.UnexpectedError;
        }
    }

    public static HostsErrorKind? ToHostsError(ElevatedExitCode code) => code switch
    {
        ElevatedExitCode.AccessDenied => HostsErrorKind.AccessDenied,
        ElevatedExitCode.ReadOnly => HostsErrorKind.ReadOnly,
        ElevatedExitCode.Locked => HostsErrorKind.Locked,
        ElevatedExitCode.ConcurrentModification => HostsErrorKind.ConcurrentModification,
        ElevatedExitCode.VerificationFailed => HostsErrorKind.VerificationFailed,
        ElevatedExitCode.UnsupportedFormat => HostsErrorKind.UnsupportedFormat,
        ElevatedExitCode.IoError => HostsErrorKind.IoError,
        _ => null,
    };
}
