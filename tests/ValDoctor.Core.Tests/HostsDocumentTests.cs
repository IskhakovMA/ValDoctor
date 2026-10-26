using System.Net;
using System.Text;
using ValDoctor.Core.Hosts;

namespace ValDoctor.Core.Tests;

public class HostsDocumentTests
{
    internal const string WindowsDefault =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
        "#\r\n" +
        "# localhost name resolution is handled within DNS itself.\r\n" +
        "#\t127.0.0.1       localhost\r\n" +
        "#\t::1             localhost\r\n" +
        "192.168.1.10    nas.local    # my NAS\r\n";

    private static readonly HostPin Auth = new("auth.riotgames.com", IPAddress.Parse("104.16.206.131"));
    private static readonly HostPin Account = new("account.riotgames.com", IPAddress.Parse("104.18.157.37"));

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static HostsDocument Doc(string s) => HostsDocument.Parse(Ascii(s));

    [Theory]
    [InlineData(WindowsDefault)]
    [InlineData("127.0.0.1 a\n# lf only\n")]
    [InlineData("mixed\r\nendings\nand\rcr")]
    [InlineData("no trailing newline")]
    [InlineData("")]
    [InlineData("\r\n\r\n\r\n")]
    public void Parse_then_serialize_round_trips_exact_bytes(string content)
    {
        var bytes = Ascii(content);
        Assert.Equal(bytes, HostsDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Round_trips_utf8_bom_with_cyrillic_comments()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Мой комментарий\r\n10.0.0.1 router # роутер\r\n")];
        var doc = HostsDocument.Parse(bytes);
        Assert.Equal(HostsTextEncoding.Utf8Bom, doc.Encoding);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void Round_trips_windows_1251_bytes_unchanged()
    {
        // "# Тест" in Windows-1251 — not valid UTF-8; must survive untouched.
        byte[] bytes = [0x23, 0x20, 0xD2, 0xE5, 0xF1, 0xF2, 0x0D, 0x0A];
        Assert.Equal(bytes, HostsDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Round_trips_utf16_le_with_bom()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("# hosts\r\n127.0.0.1 localhost\r\n")];
        var doc = HostsDocument.Parse(bytes);
        Assert.Equal(HostsTextEncoding.Utf16LittleEndian, doc.Encoding);
        Assert.Equal(bytes, doc.ToBytes());

        doc.SetPins([Auth], disableConflicts: false);
        var text = Encoding.Unicode.GetString(doc.ToBytes()[2..]);
        Assert.Contains("104.16.206.131 auth.riotgames.com", text, StringComparison.Ordinal);
        Assert.StartsWith("# hosts\r\n127.0.0.1 localhost\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_nul_bytes_without_bom()
    {
        Assert.Throws<HostsFormatException>(() => HostsDocument.Parse([0x31, 0x00, 0x32, 0x00]));
    }

    [Fact]
    public void SetPins_appends_block_and_preserves_existing_bytes()
    {
        var doc = Doc(WindowsDefault);
        var change = doc.SetPins([Auth, Account], disableConflicts: false);

        Assert.True(change.Changed);
        Assert.Equal(2, change.PinsAdded);
        var text = doc.ToText();
        Assert.StartsWith(WindowsDefault, text, StringComparison.Ordinal);
        Assert.Contains(HostsDocument.BeginMarker + "\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith(HostsDocument.EndMarker + "\r\n", text, StringComparison.Ordinal);
        Assert.Equal(2, doc.GetPins().Count);
        // Block order follows the service list, not argument order.
        Assert.True(text.IndexOf("auth.riotgames.com", StringComparison.Ordinal) < text.IndexOf("account.riotgames.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Reapplying_same_pins_is_a_no_op_and_never_duplicates()
    {
        var doc = Doc(WindowsDefault);
        doc.SetPins([Auth], false);
        var first = doc.ToBytes();

        var again = doc.SetPins([Auth], false);
        Assert.False(again.Changed);
        Assert.Equal(first, doc.ToBytes());

        // Parse from bytes (fresh process) and apply once more.
        var reparsed = HostsDocument.Parse(first);
        Assert.False(reparsed.SetPins([Auth], false).Changed);
        Assert.Single(reparsed.GetPins());
    }

    [Fact]
    public void Updating_pin_replaces_it_in_place_and_keeps_lines_after_the_block()
    {
        var doc = Doc(WindowsDefault);
        doc.SetPins([Auth], false);
        var withUserLine = HostsDocument.Parse(Ascii(doc.ToText() + "10.1.1.1 printer.local\r\n"));

        var newAuth = new HostPin(Auth.Host, IPAddress.Parse("104.16.56.21"));
        var change = withUserLine.SetPins([newAuth], false);

        Assert.True(change.Changed);
        Assert.Equal(1, change.PinsAdded);
        Assert.Equal(1, change.PinsRemoved);
        var text = withUserLine.ToText();
        Assert.DoesNotContain("104.16.206.131", text, StringComparison.Ordinal);
        Assert.Contains("104.16.56.21 auth.riotgames.com", text, StringComparison.Ordinal);
        Assert.EndsWith("10.1.1.1 printer.local\r\n", text, StringComparison.Ordinal);
        Assert.Single(withUserLine.GetPins());
    }

    [Fact]
    public void File_without_trailing_newline_gets_block_on_its_own_line()
    {
        var doc = Doc("127.0.0.1 localhost");
        doc.SetPins([Auth], false);
        Assert.StartsWith("127.0.0.1 localhost\r\n" + HostsDocument.BeginMarker, doc.ToText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# a\n1.1.1.1 x.com")]
    [InlineData("127.0.0.1 localhost")]
    [InlineData("# only comment\r\nlast")]
    public void Revert_restores_files_without_trailing_newline_exactly(string content)
    {
        var original = Ascii(content);
        var doc = HostsDocument.Parse(original);
        doc.SetPins([Auth], false);

        var again = HostsDocument.Parse(doc.ToBytes());
        Assert.False(again.SetPins([Auth], false).Changed);
        again.RevertAll();
        Assert.Equal(original, again.ToBytes());
    }

    [Fact]
    public void Fully_qualified_names_with_trailing_dot_are_conflicts()
    {
        var doc = Doc("0.0.0.0 auth.riotgames.com.\r\n");
        Assert.Single(doc.FindConflicts([Auth.Host]));
    }

    [Fact]
    public void Partial_revert_keeps_line_disabled_while_another_of_its_hosts_stays_pinned()
    {
        var doc = Doc("9.9.9.9 auth.riotgames.com account.riotgames.com\r\n");
        doc.SetPins([Auth, Account], disableConflicts: true);
        doc.Revert([Auth.Host]);
        Assert.Empty(doc.FindConflicts([Account.Host]));
        doc.Revert([Account.Host]);
        Assert.Equal("9.9.9.9 auth.riotgames.com account.riotgames.com\r\n", doc.ToText());
    }

    [Fact]
    public void Lf_files_get_lf_lines()
    {
        var doc = Doc("127.0.0.1 localhost\n");
        doc.SetPins([Auth], false);
        Assert.DoesNotContain("\r", doc.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_entry_is_a_conflict_and_is_not_overwritten_without_consent()
    {
        const string content = "1.2.3.4 auth.riotgames.com\r\n";
        var doc = Doc(content);

        var ex = Assert.Throws<HostsConflictException>(() => doc.SetPins([Auth], disableConflicts: false));
        Assert.Single(ex.Conflicts);
        Assert.True(ex.Conflicts[0].CanDisable);
        Assert.Equal(Ascii(content), doc.ToBytes());
    }

    [Fact]
    public void Conflict_detection_is_case_insensitive_and_ignores_commented_lines()
    {
        var doc = Doc("# 1.2.3.4 auth.riotgames.com\r\n5.6.7.8\tAUTH.RiotGames.com   # old fix\r\n");
        var conflicts = doc.FindConflicts([Auth.Host]);
        Assert.Single(conflicts);
        Assert.Equal(1, conflicts[0].LineIndex);
    }

    [Fact]
    public void Consented_conflict_is_disabled_and_restored_exactly_on_revert()
    {
        const string foreign = "5.6.7.8\tauth.riotgames.com   # old manual fix";
        var original = Ascii("127.0.0.1 localhost\r\n" + foreign + "\r\n");
        var doc = HostsDocument.Parse(original);

        var change = doc.SetPins([Auth], disableConflicts: true);
        Assert.Equal(1, change.LinesDisabled);
        Assert.Empty(doc.GetForeignEntries(Auth.Host));
        Assert.Single(doc.GetDisabledLines());
        Assert.Contains("# valdoctor-off:", doc.ToText(), StringComparison.Ordinal);

        var revert = HostsDocument.Parse(doc.ToBytes());
        var r = revert.RevertAll();
        Assert.Equal(1, r.LinesRestored);
        Assert.Equal(original, revert.ToBytes());
    }

    [Fact]
    public void Line_mapping_unrelated_names_is_never_disabled()
    {
        var doc = Doc("1.2.3.4 auth.riotgames.com example.com\r\n");
        var ex = Assert.Throws<HostsConflictException>(() => doc.SetPins([Auth], disableConflicts: true));
        Assert.False(ex.Conflicts[0].CanDisable);
        Assert.Equal("1.2.3.4 auth.riotgames.com example.com\r\n", doc.ToText());
    }

    [Fact]
    public void Disabled_line_is_restored_when_its_host_is_no_longer_pinned()
    {
        var doc = Doc("5.6.7.8 auth.riotgames.com\r\n");
        doc.SetPins([Auth, Account], disableConflicts: true);
        var change = doc.SetPins([Account], disableConflicts: false);
        Assert.Equal(1, change.LinesRestored);
        Assert.StartsWith("5.6.7.8 auth.riotgames.com\r\n", doc.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Full_revert_restores_the_original_bytes()
    {
        var original = Ascii(WindowsDefault);
        var doc = HostsDocument.Parse(original);
        doc.SetPins([Auth, Account], false);

        var reverted = HostsDocument.Parse(doc.ToBytes());
        var change = reverted.RevertAll();
        Assert.True(change.Changed);
        Assert.Equal(2, change.PinsRemoved);
        Assert.Equal(original, reverted.ToBytes());
        Assert.False(reverted.HasManagedContent);
    }

    [Fact]
    public void Revert_without_riotfix_content_changes_nothing()
    {
        var doc = Doc(WindowsDefault);
        Assert.False(doc.RevertAll().Changed);
        Assert.Equal(Ascii(WindowsDefault), doc.ToBytes());
    }

    [Fact]
    public void Revert_keeps_user_edits_made_after_riotfix()
    {
        var doc = Doc(WindowsDefault);
        doc.SetPins([Auth, Account], false);
        var text = doc.ToText()
            // user changed the IP of our auth line but kept the tag → it is now the user's line
            .Replace("104.16.206.131 auth.riotgames.com", "9.9.9.9 auth.riotgames.com", StringComparison.Ordinal)
            // user added their own line inside our block
            .Replace(HostsDocument.EndMarker, "10.0.0.5 mine.local\r\n" + HostsDocument.EndMarker, StringComparison.Ordinal)
            // and appended a line at the end
            + "10.0.0.6 later.local\r\n";

        var edited = Doc(text);
        Assert.Single(edited.GetPins()); // only the untouched account line is still ours
        edited.RevertAll();
        var result = edited.ToText();

        Assert.Contains("9.9.9.9 auth.riotgames.com", result, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5 mine.local", result, StringComparison.Ordinal);
        Assert.Contains("10.0.0.6 later.local", result, StringComparison.Ordinal);
        Assert.DoesNotContain("account.riotgames.com", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ValDoctor BEGIN", result, StringComparison.Ordinal);
        Assert.StartsWith(WindowsDefault, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Edited_disabled_line_is_not_restored()
    {
        var doc = Doc("5.6.7.8 auth.riotgames.com\r\n");
        doc.SetPins([Auth], disableConflicts: true);
        var tampered = Doc(doc.ToText().Replace("5.6.7.8", "5.6.7.9", StringComparison.Ordinal));
        Assert.Empty(tampered.GetDisabledLines());
        tampered.RevertAll();
        Assert.Contains("# valdoctor-off:", tampered.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_revert_removes_only_selected_hosts()
    {
        var doc = Doc(WindowsDefault);
        doc.SetPins([Auth, Account], false);

        var change = doc.Revert([Auth.Host]);
        Assert.Equal(1, change.PinsRemoved);
        Assert.Equal([Account], doc.GetPins());
        Assert.Contains("ValDoctor BEGIN", doc.ToText(), StringComparison.Ordinal);

        doc.Revert([Account.Host]);
        Assert.Equal(Ascii(WindowsDefault), doc.ToBytes());
    }

    [Fact]
    public void Partial_revert_restores_only_lines_disabled_for_those_hosts()
    {
        var doc = Doc("5.6.7.8 auth.riotgames.com\r\n5.6.7.9 account.riotgames.com\r\n");
        doc.SetPins([Auth, Account], disableConflicts: true);
        doc.Revert([Auth.Host]);
        var text = doc.ToText();
        Assert.StartsWith("5.6.7.8 auth.riotgames.com\r\n# valdoctor-off:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_pin_set_removes_block()
    {
        var doc = Doc(WindowsDefault);
        doc.SetPins([Auth], false);
        doc.SetPins([], false);
        Assert.Equal(Ascii(WindowsDefault), doc.ToBytes());
    }

    [Theory]
    [InlineData("example.com", "104.16.1.1")]
    [InlineData("auth.riotgames.com", "127.0.0.1")]
    [InlineData("auth.riotgames.com", "0.0.0.0")]
    [InlineData("auth.riotgames.com", "192.168.0.10")]
    [InlineData("auth.riotgames.com", "::1")]
    public void Rejects_hosts_outside_allowlist_and_non_public_addresses(string host, string ip)
    {
        var doc = Doc(WindowsDefault);
        Assert.Throws<ArgumentException>(() => doc.SetPins([new HostPin(host, IPAddress.Parse(ip))], false));
        Assert.Equal(Ascii(WindowsDefault), doc.ToBytes());
    }

    [Fact]
    public void Rejects_duplicate_hosts()
    {
        var doc = Doc("");
        Assert.Throws<ArgumentException>(() => doc.SetPins([Auth, Auth with { Address = IPAddress.Parse("104.16.56.21") }], false));
    }

    [Fact]
    public void Supports_ipv6_pins()
    {
        var doc = Doc("");
        var pin = new HostPin("auth.riotgames.com", IPAddress.Parse("2606:4700::6810:ce83"));
        doc.SetPins([pin], false);
        Assert.Equal([pin], HostsDocument.Parse(doc.ToBytes()).GetPins());
    }

    [Fact]
    public void Riotfix_tag_on_non_allowlisted_host_is_treated_as_foreign()
    {
        var doc = Doc("1.2.3.4 evil.example # riotfix:00000000\r\n");
        Assert.Empty(doc.GetPins());
        doc.RevertAll();
        Assert.Equal("1.2.3.4 evil.example # riotfix:00000000\r\n", doc.ToText());
    }

    [Fact]
    public void Shorthand_ipv4_is_not_treated_as_an_entry()
    {
        var doc = Doc("1 auth.riotgames.com\r\n");
        Assert.Empty(doc.GetForeignEntries());
    }
}
