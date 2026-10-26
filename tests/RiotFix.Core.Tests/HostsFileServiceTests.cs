using System.Net;
using System.Text;
using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;
using RiotFix.Core.Tests.Fakes;

namespace RiotFix.Core.Tests;

public class HostsFileServiceTests
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    private static readonly HostPin Auth = new("auth.riotgames.com", IPAddress.Parse("104.16.206.131"));
    private static readonly byte[] Original = Encoding.ASCII.GetBytes(HostsDocumentTests.WindowsDefault);

    private static (FakeFileSystem Fs, HostsFileService Service) Create(byte[]? content = null, FileAttributes attributes = FileAttributes.Normal)
    {
        var fs = new FakeFileSystem();
        if (content is not null)
        {
            fs.Seed(HostsPath, content, attributes);
        }

        return (fs, new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { }));
    }

    [Fact]
    public void First_change_creates_original_backup_and_writes_atomically()
    {
        var (fs, service) = Create(Original);

        var result = service.Update(d => d.SetPins([Auth], false));

        Assert.True(result.Written);
        Assert.Equal(Original, fs.Get(service.OriginalBackupPath));
        Assert.Equal(Original, fs.Get(service.PreviousBackupPath));
        Assert.False(fs.Has(service.TempPath));
        Assert.Contains("replace:hosts", fs.Operations);
        Assert.Contains("104.16.206.131 auth.riotgames.com", Encoding.ASCII.GetString(fs.Get(HostsPath)), StringComparison.Ordinal);
    }

    [Fact]
    public void Original_backup_is_written_only_once()
    {
        var (fs, service) = Create(Original);
        service.Update(d => d.SetPins([Auth], false));
        var afterFirst = fs.Get(HostsPath);

        service.Update(d => d.SetPins([Auth with { Address = IPAddress.Parse("104.16.56.21") }], false));

        Assert.Equal(Original, fs.Get(service.OriginalBackupPath));
        Assert.Equal(afterFirst, fs.Get(service.PreviousBackupPath));
    }

    [Fact]
    public void No_change_means_no_write_and_no_backup()
    {
        var (fs, service) = Create(Original);
        var result = service.Update(d => d.RevertAll());

        Assert.False(result.Written);
        Assert.False(fs.Has(service.OriginalBackupPath));
        Assert.DoesNotContain(fs.Operations, o => o.StartsWith("write", StringComparison.Ordinal) || o.StartsWith("replace", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_only_hosts_is_reported_and_left_untouched()
    {
        var (fs, service) = Create(Original, FileAttributes.ReadOnly);
        var ex = Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([Auth], false)));
        Assert.Equal(HostsErrorKind.ReadOnly, ex.Kind);
        Assert.Equal(Original, fs.Get(HostsPath));
        Assert.False(fs.Has(service.TempPath));
    }

    [Fact]
    public void Access_denied_is_reported_and_temp_file_cleaned_up()
    {
        var (fs, service) = Create(Original);
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace")
            {
                throw new UnauthorizedAccessException("denied");
            }
        };

        var ex = Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([Auth], false)));
        Assert.Equal(HostsErrorKind.AccessDenied, ex.Kind);
        Assert.Equal(Original, fs.Get(HostsPath));
        Assert.False(fs.Has(service.TempPath));
    }

    [Fact]
    public void Access_denied_on_read_is_reported()
    {
        var (fs, service) = Create(Original);
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "read")
            {
                throw new UnauthorizedAccessException("denied");
            }
        };

        Assert.Equal(HostsErrorKind.AccessDenied, Assert.Throws<HostsAccessException>(service.Load).Kind);
    }

    [Fact]
    public void Locked_file_is_retried_then_succeeds()
    {
        var (fs, service) = Create(Original);
        var failures = 2;
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace" && failures-- > 0)
            {
                throw IoErrors.SharingViolation();
            }
        };

        Assert.True(service.Update(d => d.SetPins([Auth], false)).Written);
    }

    [Fact]
    public void Permanently_locked_file_reports_locked()
    {
        var (fs, service) = Create(Original);
        fs.BeforeOperation = (op, _) =>
        {
            if (op == "replace")
            {
                throw IoErrors.SharingViolation();
            }
        };

        Assert.Equal(HostsErrorKind.Locked, Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([Auth], false))).Kind);
        Assert.Equal(Original, fs.Get(HostsPath));
    }

    [Fact]
    public void Concurrent_change_is_detected_and_preserved()
    {
        var (fs, service) = Create(Original);
        var injected = false;
        fs.BeforeOperation = (op, path) =>
        {
            // Another program appends a line right after we wrote the temp file.
            if (!injected && op == "write" && path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                injected = true;
                fs.SetRaw(HostsPath, [.. Original, .. Encoding.ASCII.GetBytes("10.9.9.9 other.local\r\n")]);
            }
        };

        var result = service.Update(d => d.SetPins([Auth], false));

        Assert.True(result.Written);
        var text = Encoding.ASCII.GetString(fs.Get(HostsPath));
        Assert.Contains("10.9.9.9 other.local", text, StringComparison.Ordinal);
        Assert.Contains("auth.riotgames.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constant_concurrent_changes_abort_without_writing()
    {
        var (fs, service) = Create(Original);
        var n = 0;
        fs.BeforeOperation = (op, path) =>
        {
            if (op == "write" && path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                fs.SetRaw(HostsPath, [.. Original, .. Encoding.ASCII.GetBytes($"# change {n++}\r\n")]);
            }
        };

        var ex = Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([Auth], false)));
        Assert.Equal(HostsErrorKind.ConcurrentModification, ex.Kind);
        Assert.DoesNotContain("auth.riotgames.com", Encoding.ASCII.GetString(fs.Get(HostsPath)), StringComparison.Ordinal);
        Assert.False(fs.Has(service.TempPath));
    }

    [Fact]
    public void Verification_failure_is_reported()
    {
        var (fs, service) = Create(Original);
        fs.CorruptReplaceWith = Encoding.ASCII.GetBytes("garbage");
        Assert.Equal(HostsErrorKind.VerificationFailed, Assert.Throws<HostsAccessException>(() => service.Update(d => d.SetPins([Auth], false))).Kind);
    }

    [Fact]
    public void Missing_hosts_file_is_created()
    {
        var (fs, service) = Create(content: null);
        Assert.True(service.Update(d => d.SetPins([Auth], false)).Written);
        Assert.Contains("move:hosts", fs.Operations);
        Assert.Single(HostsDocument.Parse(fs.Get(HostsPath)).GetPins());
        Assert.False(fs.Has(service.OriginalBackupPath));
    }

    [Fact]
    public void Unsupported_format_is_reported()
    {
        var (_, service) = Create([0x41, 0x00, 0x42, 0x00]);
        Assert.Equal(HostsErrorKind.UnsupportedFormat, Assert.Throws<HostsAccessException>(service.Load).Kind);
    }

    [Fact]
    public void Conflict_leaves_file_untouched()
    {
        var content = Encoding.ASCII.GetBytes("5.6.7.8 auth.riotgames.com\r\n");
        var (fs, service) = Create(content);
        Assert.Throws<HostsConflictException>(() => service.Update(d => d.SetPins([Auth], false)));
        Assert.Equal(content, fs.Get(HostsPath));
        Assert.False(fs.Has(service.OriginalBackupPath));
    }

    [Fact]
    public void Real_file_system_apply_and_revert_round_trip_in_temp_directory()
    {
        var dir = Directory.CreateTempSubdirectory("riotfix-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "hosts");
            File.WriteAllBytes(path, Original);
            var service = new HostsFileService(new PhysicalFileSystem(), path, NullLog.Instance);

            Assert.True(service.Update(d => d.SetPins([Auth], false)).Written);
            Assert.Single(service.Load().GetPins());
            Assert.True(File.Exists(service.OriginalBackupPath));

            Assert.True(service.Update(d => d.RevertAll()).Written);
            Assert.Equal(Original, File.ReadAllBytes(path));
            Assert.False(File.Exists(service.TempPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
