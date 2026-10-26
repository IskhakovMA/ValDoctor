using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ValDoctor.Core.Diagnostics;
using ValDoctor.Core.Elevation;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Legacy;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Tests.Fakes;
using ValDoctor.Core.Updates;

namespace ValDoctor.Core.Tests;

/// <summary>ValDoctor (formerly RiotFix): everything RiotFix 0.1.0–0.2.1 left on a PC keeps working.</summary>
public class RiotFixCompatibilityTests
{
    private const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";

    // Exactly what RiotFix 0.2.1 writes (format frozen; never regenerate from current code).
    private const string Before = "# Copyright (c) 1993-2009 Microsoft Corp.\r\n127.0.0.1 router.local\r\n5.6.7.8 auth.riotgames.com\r\n";
    private const string WrittenByRiotFix =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "127.0.0.1 router.local\r\n" +
        "# riotfix-off:d71553f8 5.6.7.8 auth.riotgames.com\r\n" +
        "# >>> RiotFix BEGIN (managed block, https://github.com/IskhakovMA/riotfix)\r\n" +
        "104.16.120.50 auth.riotgames.com # riotfix:fd5fb184\r\n" +
        "# <<< RiotFix END\r\n";

    private static readonly HostPin Auth = new("auth.riotgames.com", IPAddress.Parse("104.16.120.50"));

    [Fact]
    public void Riotfix_entries_are_recognised_and_fully_reverted_to_the_original_bytes()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(WrittenByRiotFix));
        Assert.True(doc.HasManagedContent);
        Assert.Equal([Auth], doc.GetPins());

        doc.RevertAll();
        Assert.Equal(Before, doc.ToText());
    }

    [Fact]
    public void New_writes_use_the_valdoctor_format_and_are_idempotent()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes("127.0.0.1 localhost\r\n"));
        doc.SetPins([Auth], false);
        var text = doc.ToText();
        Assert.Contains("# >>> ValDoctor BEGIN", text, StringComparison.Ordinal);
        Assert.Contains("104.16.120.50 auth.riotgames.com # valdoctor:fd5fb184", text, StringComparison.Ordinal);
        Assert.DoesNotContain("riotfix", text.Replace("IskhakovMA/riotfix", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.False(HostsDocument.Parse(doc.ToBytes()).SetPins([Auth], false).Changed);
    }

    [Fact]
    public void Applying_over_a_riotfix_block_converts_it_in_place_and_revert_still_restores_the_original()
    {
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(WrittenByRiotFix));
        doc.SetPins([Auth with { Address = IPAddress.Parse("104.16.119.50") }], disableConflicts: true);
        var converted = HostsDocument.Parse(doc.ToBytes());
        Assert.DoesNotContain("RiotFix BEGIN", converted.ToText(), StringComparison.Ordinal);
        Assert.Single(converted.GetPins());

        converted.RevertAll();
        Assert.Equal(Before, converted.ToText());
    }

    [Fact]
    public void Mixed_riotfix_and_valdoctor_lines_are_all_rolled_back()
    {
        var mixed = WrittenByRiotFix +
                    HostsDocument.BeginMarker + "\r\n" +
                    HostsDocument.PinLine(new HostPin("account.riotgames.com", IPAddress.Parse("104.16.56.40"))) + "\r\n" +
                    HostsDocument.EndMarker + "\r\n";
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(mixed));
        Assert.Equal(2, doc.GetPins().Count);

        doc.RevertAll();
        Assert.Equal(Before, doc.ToText());
    }

    [Fact]
    public void Edited_riotfix_line_belongs_to_the_user()
    {
        var edited = WrittenByRiotFix.Replace("104.16.120.50 auth", "104.16.120.51 auth", StringComparison.Ordinal);
        var doc = HostsDocument.Parse(Encoding.ASCII.GetBytes(edited));
        Assert.Empty(doc.GetPins());
        doc.RevertAll();
        Assert.Contains("104.16.120.51 auth.riotgames.com # riotfix:fd5fb184", doc.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_riotfix_original_backup_stays_the_original()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Encoding.ASCII.GetBytes(WrittenByRiotFix));
        var legacyOriginal = Encoding.ASCII.GetBytes(Before);
        fs.Seed(HostsPath + RiotFixLegacy.OriginalBackupSuffix, legacyOriginal);
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });

        Assert.True(service.Update(d => d.SetPins([Auth with { Address = IPAddress.Parse("104.16.119.50") }], true)).Written);

        Assert.False(fs.Has(service.OriginalBackupPath));                      // no second "original"
        Assert.Equal(legacyOriginal, fs.Get(service.LegacyOriginalBackupPath)); // RiotFix backup untouched
        Assert.Equal(service.LegacyOriginalBackupPath, service.EffectiveOriginalBackupPath);
        Assert.True(fs.Has(service.PreviousBackupPath));
    }

    [Fact]
    public void Without_riotfix_backup_valdoctor_creates_its_own_original()
    {
        var fs = new FakeFileSystem();
        fs.Seed(HostsPath, Encoding.ASCII.GetBytes(Before));
        var service = new HostsFileService(fs, HostsPath, NullLog.Instance, _ => { });
        service.Update(d => d.SetPins([Auth], true));
        Assert.EndsWith(".valdoctor-original.bak", service.EffectiveOriginalBackupPath, StringComparison.Ordinal);
        Assert.Equal(Encoding.ASCII.GetBytes(Before), fs.Get(service.OriginalBackupPath));
    }

    [Fact]
    public void Elevated_helper_uses_the_valdoctor_flag_only()
    {
        Assert.Equal("--valdoctor-elevated", ElevatedProtocol.Flag);
        Assert.False(ElevatedProtocol.IsElevatedInvocation(["--riotfix-elevated", "revert"]));
        Assert.Equal(ElevatedOperation.Revert, ElevatedProtocol.Parse(ElevatedProtocol.ToArguments(ElevatedRequest.Revert())).Operation);
    }

    [Fact]
    public void Riotfix_update_state_is_read_but_never_modified()
    {
        var dir = Directory.CreateTempSubdirectory("valdoctor-state-");
        try
        {
            var legacy = Path.Join(dir.FullName, "RiotFix", "update-state.json");
            var current = Path.Join(dir.FullName, "ValDoctor", "update-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            const string legacyJson = "{\"LastCheckUtc\":\"2026-10-26T12:00:00+00:00\",\"DismissedTag\":\"v0.2.1\"}";
            File.WriteAllText(legacy, legacyJson);

            var store = new FileUpdateStateStore(current, legacy);
            var state = store.Load();
            Assert.Equal("v0.2.1", state.DismissedTag);

            store.Save(state with { DismissedTag = "v0.3.1" });
            Assert.Equal(legacyJson, File.ReadAllText(legacy));
            Assert.Equal("v0.3.1", new FileUpdateStateStore(current, legacy).Load().DismissedTag);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Log_cleanup_never_touches_riotfix_logs()
    {
        var dir = Directory.CreateTempSubdirectory("valdoctor-logs-");
        try
        {
            for (var i = 0; i < 15; i++)
            {
                File.WriteAllText(Path.Join(dir.FullName, $"riotfix-202610{i:00}.log"), "old");
            }

            var log = new FileLog(dir.FullName);
            log.Info("ValDoctor started");

            Assert.Equal(15, Directory.GetFiles(dir.FullName, "riotfix-*.log").Length);
            Assert.Single(Directory.GetFiles(dir.FullName, $"{FileLog.FilePrefix}*.log"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

/// <summary>Update check keeps working for RiotFix users today and for ValDoctor after a possible repository rename.</summary>
public class RebrandUpdateTests
{
    private static string Release(string tag, string repo) =>
        $$"""{"tag_name":"{{tag}}","name":"ValDoctor {{tag}}","draft":false,"prerelease":true,"html_url":"https://github.com/IskhakovMA/{{repo}}/releases/tag/{{tag}}","body":"x"}""";

    [Fact]
    public void Releases_are_still_published_in_the_riotfix_repository()
    {
        Assert.Equal("https://api.github.com/repos/IskhakovMA/riotfix/releases?per_page=20", UpdateChecker.ReleasesApi.AbsoluteUri);
        Assert.Equal(RiotFixLegacy.GitHubRepository, UpdateChecker.Repository);
    }

    [Theory]
    [InlineData("riotfix", true)]
    [InlineData("valdoctor", true)]
    [InlineData("valdoctor-fake", false)]
    [InlineData("other", false)]
    public void Release_links_of_the_current_or_renamed_repository_are_accepted(string repo, bool accepted)
    {
        var releases = UpdateChecker.ParseReleases(Encoding.UTF8.GetBytes($"[{Release("v0.3.1", repo)}]"));
        Assert.Equal(accepted, releases.Count == 1);
        if (accepted)
        {
            Assert.Equal($"https://github.com/IskhakovMA/{repo}/releases/tag/v0.3.1", releases[0].Url.AbsoluteUri);
        }
    }

    [Fact]
    public async Task Redirect_from_a_renamed_repository_is_followed_on_api_github_com_only()
    {
        var calls = new List<Uri>();
        var http = new FakeHttp((request, _) =>
        {
            calls.Add(request.RequestUri!);
            if (calls.Count == 1)
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri("https://api.github.com/repositories/123456/releases?per_page=20");
                return Task.FromResult(moved);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{Release("v0.3.1", "valdoctor")}]", Encoding.UTF8, "application/json"),
            });
        });

        var update = await new UpdateChecker(new HttpMessageInvoker(http), new MemoryStore(), Parse("0.3.0")).CheckAsync(CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal("0.3.1", update!.Version.ToString());
    }

    [Fact]
    public async Task Redirect_to_another_host_is_refused_silently()
    {
        var http = new FakeHttp((_, _) =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://evil.example/releases");
            return Task.FromResult(moved);
        });

        Assert.Null(await new UpdateChecker(new HttpMessageInvoker(http), new MemoryStore(), Parse("0.3.0")).CheckAsync(CancellationToken.None));
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public void Riotfix_versions_are_older_than_valdoctor_0_3_0()
    {
        foreach (var old in new[] { "v0.1.0", "v0.2.0", "v0.2.1" })
        {
            Assert.True(Parse("0.3.0") > Parse(old));
        }
    }

    private static SemVersion Parse(string v) => SemVersion.TryParse(v, out var s) ? s : throw new ArgumentException(v);

    private sealed class MemoryStore : IUpdateStateStore
    {
        private UpdateState _state = new();

        public UpdateState Load() => _state;

        public void Save(UpdateState state) => _state = state;
    }
}

/// <summary>The product is called ValDoctor everywhere the user can see it.</summary>
public partial class ProductNameTests
{
    [Fact]
    public void User_facing_texts_say_valdoctor()
    {
        var report = new DiagnosisReport(DateTimeOffset.Now, TimeSpan.Zero,
            [ClassificationTests.Domain("auth.riotgames.com", DomainHealth.Failed)], false, true, false, [], FixPlan.Empty, Verdict.ProblemsWithoutFix);
        var text = ReportFormatter.Format(report, "0.3.0");
        Assert.StartsWith("ValDoctor 0.3.0 — отчёт о проверке", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RiotFix", text, StringComparison.Ordinal);
        foreach (Verdict v in Enum.GetValues<Verdict>())
        {
            var msg = Texts.ForReport(report with { Verdict = v });
            Assert.DoesNotContain("RiotFix", msg.Headline + msg.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Riotfix_string_literals_exist_only_in_the_legacy_layer()
    {
        var root = Root();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Join(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal) ||
                                  f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".manifest", StringComparison.Ordinal)) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var isLegacyLayer = file.EndsWith("RiotFixLegacy.cs", StringComparison.Ordinal);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Replace("IskhakovMA/riotfix", string.Empty, StringComparison.Ordinal);
                var code = line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line;
                var literal = StringLiteral().Matches(code).Any(m => m.Value.Contains("riotfix", StringComparison.OrdinalIgnoreCase));
                var xamlOrProject = !file.EndsWith(".cs", StringComparison.Ordinal) && line.Contains("riotfix", StringComparison.OrdinalIgnoreCase);
                if (!isLegacyLayer && (literal || xamlOrProject))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Affiliation_note_is_not_shown_in_the_app_but_stays_in_public_docs()
    {
        var root = Root();
        foreach (var file in Directory.GetFiles(Path.Join(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("Не связано с Riot Games", File.ReadAllText(file), StringComparison.Ordinal);
        }

        // The independence statement remains in the README and in the release notes template.
        Assert.Contains("не связан с Riot Games", File.ReadAllText(Path.Join(root, "README.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не связан с Riot Games", File.ReadAllText(Path.Join(root, ".github", "release-notes.md")), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "ValDoctor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
