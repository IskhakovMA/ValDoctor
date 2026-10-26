using System.Security.Cryptography;

namespace ValDoctor.Core.Hosts;

/// <summary>Content hash of the hosts file, used to detect that it changed between diagnosis and fix.</summary>
public static class HostsFingerprint
{
    public static string Of(HostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Convert.ToHexStringLower(SHA256.HashData(document.ToBytes()));
    }
}
