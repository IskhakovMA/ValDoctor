namespace RiotFix.Core;

/// <summary>A Riot Games host name that RiotFix diagnoses and may pin in the hosts file.</summary>
public sealed record RiotService(string Host, string DisplayName);

/// <summary>
/// The fixed allowlist of host names RiotFix works with. RiotFix never writes hosts entries
/// for any other name, and the elevated helper rejects everything outside this list.
/// </summary>
public static class RiotServices
{
    public static IReadOnlyList<RiotService> All { get; } =
    [
        new("auth.riotgames.com", "Вход в аккаунт"),
        new("account.riotgames.com", "Учётная запись"),
        new("clientconfig.rpg.riotgames.com", "Настройки клиента"),
        new("entitlements.auth.riotgames.com", "Доступ к играм"),
        new("data.riotgames.com", "Служба данных"),
        new("sieve.services.riotcdn.net", "Служебный сервис клиента"),
    ];

    public static bool IsAllowed(string host) => Find(host) is not null;

    public static RiotService? Find(string host) =>
        All.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable ordering used when writing the managed hosts block.</summary>
    public static int OrderOf(string host)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
