namespace ValDoctor.Core.Network;

public interface IProxyDetector
{
    /// <summary>True when the system (WinINET/env) proxy would be used for HTTPS requests to <paramref name="host"/>.</summary>
    bool IsProxyConfigured(string host);
}

public sealed class SystemProxyDetector : IProxyDetector
{
    public bool IsProxyConfigured(string host)
    {
        try
        {
            var target = new Uri($"https://{host}/");
            var proxy = HttpClient.DefaultProxy;
            return !proxy.IsBypassed(target) && proxy.GetProxy(target) is { } p && p != target;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
