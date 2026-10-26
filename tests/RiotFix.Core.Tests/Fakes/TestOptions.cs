using RiotFix.Core.Diagnostics;

namespace RiotFix.Core.Tests.Fakes;

/// <summary>Product timings, except that DNS retries do not wait seconds in unit tests.</summary>
public static class TestOptions
{
    public static DiagnosticsOptions Fast { get; } = new() { DnsRetryDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)] };
}
