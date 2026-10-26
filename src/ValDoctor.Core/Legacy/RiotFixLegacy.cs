namespace ValDoctor.Core.Legacy;

/// <summary>
/// Names used by RiotFix 0.1.0–0.2.1, the previous name of ValDoctor. They are part of data that already exists on
/// users' computers, so ValDoctor keeps recognising them forever: hosts lines written by RiotFix are detected, updated
/// and rolled back; existing backups count as the original backup; settings in the old folder are read.
/// ValDoctor never writes new data in these formats.
/// </summary>
public static class RiotFixLegacy
{
    /// <summary>Hosts block markers written by RiotFix.</summary>
    public const string HostsBeginMarkerPrefix = "# >>> RiotFix BEGIN";
    public const string HostsEndMarker = "# <<< RiotFix END";

    /// <summary>Tag of a RiotFix pin line: <c>IP HOST # riotfix:XXXXXXXX</c>.</summary>
    public const string PinTagPrefix = "# riotfix:";

    /// <summary>Third-party line commented out by RiotFix: <c># riotfix-off:XXXXXXXX ORIGINAL</c>.</summary>
    public const string DisabledPrefix = "# riotfix-off:";

    /// <summary>Backups RiotFix created next to the hosts file.</summary>
    public const string OriginalBackupSuffix = ".riotfix-original.bak";
    public const string PreviousBackupSuffix = ".riotfix-previous.bak";

    /// <summary>Per-user folder under %LocalAppData% (logs, update-check state).</summary>
    public const string AppDataFolder = "RiotFix";

    /// <summary>
    /// GitHub repository of the project. Kept as is: every installed RiotFix asks this repository for updates and
    /// accepts release links only from it, so renaming it would silently stop update notices for those users.
    /// </summary>
    public const string GitHubRepository = "riotfix";
}
