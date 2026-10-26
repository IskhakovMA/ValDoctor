using ValDoctor.Core.Legacy;
using ValDoctor.Core.Logging;

namespace ValDoctor.Core.Hosts;

public enum HostsErrorKind
{
    AccessDenied,
    ReadOnly,
    Locked,
    ConcurrentModification,
    VerificationFailed,
    UnsupportedFormat,
    IoError,
}

public sealed class HostsAccessException : Exception
{
    public HostsAccessException(HostsErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public HostsAccessException()
        : this(HostsErrorKind.IoError, "Hosts file error.")
    {
    }

    public HostsAccessException(string message)
        : this(HostsErrorKind.IoError, message)
    {
    }

    public HostsAccessException(string message, Exception innerException)
        : this(HostsErrorKind.IoError, message, innerException)
    {
    }

    public HostsErrorKind Kind { get; }
}

public sealed record HostsUpdateResult(HostsChange Change, bool Written);

/// <summary>
/// Reads and safely rewrites the hosts file.
/// <list type="number">
/// <item>Backups live next to the hosts file (only administrators can write there, so the backup path cannot be redirected by a non-admin user).</item>
/// <item>The original pre-ValDoctor file is backed up once; the state before every change is kept as the "previous" backup.</item>
/// <item>The new content is written to a temporary file, flushed, and swapped in with an atomic replace that keeps the file's ACL.</item>
/// <item>If the file changed while we were working (another program or the user), the operation is recomputed from the fresh content.</item>
/// <item>After writing, the file is read back and compared.</item>
/// </list>
/// </summary>
public sealed class HostsFileService
{
    private const int MaxAttempts = 5;
    private readonly IFileSystem _fs;
    private readonly IAppLog _log;
    private readonly Action<TimeSpan> _sleep;

    public HostsFileService(IFileSystem fileSystem, string hostsPath, IAppLog log, Action<TimeSpan>? sleep = null)
    {
        _fs = fileSystem;
        HostsPath = hostsPath;
        _log = log;
        _sleep = sleep ?? Thread.Sleep;
    }

    public string HostsPath { get; }

    public string OriginalBackupPath => HostsPath + ".valdoctor-original.bak";

    public string PreviousBackupPath => HostsPath + ".valdoctor-previous.bak";

    public string TempPath => HostsPath + ".valdoctor.tmp";

    internal string OriginalBackupTempPath => OriginalBackupPath + ".tmp";

    /// <summary>The pre-change backup made by RiotFix (0.1.0–0.2.1), if any. It stays the true original and is never touched.</summary>
    public string LegacyOriginalBackupPath => HostsPath + RiotFixLegacy.OriginalBackupSuffix;

    /// <summary>The backup that holds the state before the very first change: RiotFix's if it exists, otherwise ValDoctor's.</summary>
    public string EffectiveOriginalBackupPath =>
        Guard(() => _fs.FileExists(LegacyOriginalBackupPath)) ? LegacyOriginalBackupPath : OriginalBackupPath;

    public HostsDocument Load()
    {
        var (bytes, _) = ReadCurrent();
        return ParseOrThrow(bytes);
    }

    /// <summary>Applies <paramref name="mutate"/> to the current file content and writes the result if anything changed.</summary>
    public HostsUpdateResult Update(Func<HostsDocument, HostsChange> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var (original, existed) = ReadCurrent();
            var document = ParseOrThrow(original);
            var change = mutate(document);
            if (!change.Changed)
            {
                return new HostsUpdateResult(change, Written: false);
            }

            if (existed && Guard(() => _fs.GetAttributes(HostsPath)).HasFlag(FileAttributes.ReadOnly))
            {
                throw new HostsAccessException(HostsErrorKind.ReadOnly, "The hosts file has the read-only attribute.");
            }

            var updated = document.ToBytes();

            try
            {
                if (existed)
                {
                    // Only before the very first change, by ValDoctor or by RiotFix: an existing RiotFix original stays the original.
                    if (!Guard(() => _fs.FileExists(OriginalBackupPath)) && !Guard(() => _fs.FileExists(LegacyOriginalBackupPath)))
                    {
                        // Written to a temporary name first, so an interrupted write never leaves a truncated "original".
                        Guard(() => _fs.WriteAllBytesDurable(OriginalBackupTempPath, original));
                        Guard(() => _fs.Move(OriginalBackupTempPath, OriginalBackupPath));
                        _log.Info("Hosts: original backup created.");
                    }

                    Guard(() => _fs.WriteAllBytesDurable(PreviousBackupPath, original));
                }

                Guard(() => _fs.WriteAllBytesDurable(TempPath, updated));

                var (current, stillExists) = ReadCurrent();
                if (stillExists != existed || !current.AsSpan().SequenceEqual(original))
                {
                    _log.Warn($"Hosts: file changed by another program during update (attempt {attempt}), retrying.");
                    TryDelete(TempPath);
                    continue;
                }

                if (existed)
                {
                    Guard(() => _fs.Replace(TempPath, HostsPath));
                }
                else
                {
                    Guard(() => _fs.Move(TempPath, HostsPath));
                }
            }
            catch (HostsAccessException ex) when (ex.Kind == HostsErrorKind.Locked && attempt < MaxAttempts)
            {
                // Another program holds the file, often because it is writing it right now: start over from a fresh
                // read instead of retrying only the swap, so that program's change is not overwritten.
                RestoreIfHostsMissing(existed, original);
                TryDelete(TempPath);
                TryDelete(OriginalBackupTempPath);
                _log.Warn($"Hosts: file locked (attempt {attempt}), retrying from a fresh read.");
                _sleep(TimeSpan.FromMilliseconds(250 * attempt));
                continue;
            }
            catch
            {
                RestoreIfHostsMissing(existed, original);
                TryDelete(TempPath);
                TryDelete(OriginalBackupTempPath);
                throw;
            }

            var (written, _) = ReadCurrent();
            if (!written.AsSpan().SequenceEqual(updated))
            {
                throw new HostsAccessException(HostsErrorKind.VerificationFailed, "The hosts file content after writing does not match the expected content.");
            }

            _log.Info($"Hosts: updated (+{change.PinsAdded} pins, -{change.PinsRemoved} pins, {change.LinesDisabled} disabled, {change.LinesRestored} restored).");
            return new HostsUpdateResult(change, Written: true);
        }

        throw new HostsAccessException(HostsErrorKind.ConcurrentModification, "The hosts file keeps changing while ValDoctor tries to update it.");
    }

    /// <summary>
    /// ReplaceFile can fail after it has already removed the destination (ERROR_UNABLE_TO_MOVE_REPLACEMENT). The system
    /// must never be left without a hosts file: put the original content back.
    /// </summary>
    private void RestoreIfHostsMissing(bool existed, byte[] original)
    {
        try
        {
            if (existed && !_fs.FileExists(HostsPath))
            {
                _fs.WriteAllBytesDurable(HostsPath, original);
                _log.Warn("Hosts: the file disappeared during the update and was restored to its previous content.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Hosts: could not restore the hosts file; the previous content is in the 'previous' backup.", ex);
        }
    }

    private (byte[] Bytes, bool Existed) ReadCurrent()
    {
        if (!Guard(() => _fs.FileExists(HostsPath)))
        {
            return ([], false);
        }

        return (Guard(() => _fs.ReadAllBytes(HostsPath)), true);
    }

    private static HostsDocument ParseOrThrow(byte[] bytes)
    {
        try
        {
            return HostsDocument.Parse(bytes);
        }
        catch (HostsFormatException ex)
        {
            throw new HostsAccessException(HostsErrorKind.UnsupportedFormat, ex.Message, ex);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_fs.FileExists(path))
            {
                _fs.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Hosts: could not delete temporary file ({ex.GetType().Name}).");
        }
    }

    private static void Guard(Action action) => Guard(() =>
    {
        action();
        return true;
    });

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (HostsAccessException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HostsAccessException(HostsErrorKind.AccessDenied, "Access to the hosts file was denied.", ex);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            throw new HostsAccessException(HostsErrorKind.Locked, "The hosts file is locked by another program.", ex);
        }
        catch (IOException ex)
        {
            throw new HostsAccessException(HostsErrorKind.IoError, "The hosts file could not be read or written.", ex);
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33), ERROR_USER_MAPPED_FILE (1224)
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33 or 1224;
    }
}
