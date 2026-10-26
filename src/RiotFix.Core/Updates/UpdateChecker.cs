using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiotFix.Core.Logging;

namespace RiotFix.Core.Updates;

/// <summary>A published GitHub release of RiotFix.</summary>
public sealed record ReleaseInfo(SemVersion Version, string Tag, string Title, string Summary, Uri Url, bool Prerelease);

/// <summary>Persisted between launches: when we last asked GitHub, what we saw, and what the user dismissed.</summary>
public sealed record UpdateState
{
    public DateTimeOffset? LastCheckUtc { get; init; }

    public string? LatestTag { get; init; }

    public string? LatestTitle { get; init; }

    public string? LatestSummary { get; init; }

    public bool LatestPrerelease { get; init; }

    public string? DismissedTag { get; init; }
}

public interface IUpdateStateStore
{
    UpdateState Load();

    void Save(UpdateState state);
}

/// <summary>
/// Checks GitHub Releases for a newer RiotFix at most once per 24 hours. One anonymous GET to
/// api.github.com, short timeout, bounded response size, no telemetry, nothing is downloaded or run —
/// the user only gets a link to the official release page. Any failure is silent.
/// </summary>
public sealed class UpdateChecker
{
    public const string Owner = "IskhakovMA";
    public const string Repository = "riotfix";
    public static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=20");
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const int MaxResponseBytes = 512 * 1024;
    private const string ReleasePagePrefix = $"https://github.com/{Owner}/{Repository}/releases/tag/";

    private readonly HttpMessageInvoker _http;
    private readonly IUpdateStateStore _store;
    private readonly TimeProvider _time;
    private readonly SemVersion _current;
    private readonly IRiotFixLog _log;
    private readonly TimeSpan _timeout;

    public UpdateChecker(HttpMessageInvoker http, IUpdateStateStore store, SemVersion current, TimeProvider? time = null, IRiotFixLog? log = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? Timeout;
        _http = http;
        _store = store;
        _current = current;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>Prereleases are offered while RiotFix itself is 0.x or a prerelease; stable builds only see stable releases.</summary>
    public bool IncludePrereleases => _current.Major == 0 || _current.IsPrerelease;

    /// <summary>Returns a newer release the user has not dismissed, or null. Never throws (except on cancellation).</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        var state = SafeLoad();
        var now = _time.GetUtcNow();
        if (state.LastCheckUtc is { } last && now - last < CheckInterval && now >= last)
        {
            return Offer(FromState(state), state);
        }

        ReleaseInfo? latest;
        try
        {
            latest = await FetchLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException)
        {
            // Offline or GitHub unavailable: keep working silently, try again next launch.
            _log.Info($"Update check skipped: {ex.GetType().Name}.");
            return Offer(FromState(state), state);
        }

        state = state with
        {
            LastCheckUtc = now,
            LatestTag = latest?.Tag,
            LatestTitle = latest?.Title,
            LatestSummary = latest?.Summary,
            LatestPrerelease = latest?.Prerelease ?? false,
        };
        SafeSave(state);
        return Offer(latest, state);
    }

    public void Dismiss(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        SafeSave(SafeLoad() with { DismissedTag = release.Tag });
    }

    /// <summary>Parses the GitHub "list releases" JSON and returns valid, published releases.</summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(ReadOnlySpan<byte> json)
    {
        var items = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ListGitHubRelease) ?? [];
        var result = new List<ReleaseInfo>();
        foreach (var item in items)
        {
            if (item is null || item.Draft || item.TagName is null || !SemVersion.TryParse(item.TagName, out var version))
            {
                continue;
            }

            // The link is rebuilt from a validated tag; whatever html_url says is not trusted blindly.
            var tag = item.TagName.Trim();
            if (!tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
            {
                continue;
            }

            var url = new Uri(ReleasePagePrefix + Uri.EscapeDataString(tag));
            if (item.HtmlUrl is not null && !string.Equals(item.HtmlUrl, url.AbsoluteUri, StringComparison.Ordinal))
            {
                continue;
            }

            var title = Clean(item.Name, 80);
            result.Add(new ReleaseInfo(version, tag, string.IsNullOrEmpty(title) ? $"RiotFix {tag}" : title,
                Summary(item.Body), url, item.Prerelease || version.IsPrerelease));
        }

        return result;
    }

    public static Uri ReleaseUrl(string tag) => new(ReleasePagePrefix + Uri.EscapeDataString(tag));

    internal ReleaseInfo? SelectNewest(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => IncludePrereleases || !r.Prerelease)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    /// <summary>First meaningful line of the release notes, without markdown decoration.</summary>
    internal static string Summary(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        foreach (var raw in body.Split('\n'))
        {
            var line = Clean(raw.Trim().TrimStart('#', '*', '-', '>', ' '), 160);
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (!char.IsControl(c) && c is not '*' and not '`')
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString().Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    private ReleaseInfo? Offer(ReleaseInfo? latest, UpdateState state) =>
        latest is not null && latest.Version > _current && (IncludePrereleases || !latest.Prerelease) &&
        !string.Equals(latest.Tag, state.DismissedTag, StringComparison.OrdinalIgnoreCase)
            ? latest
            : null;

    private static ReleaseInfo? FromState(UpdateState state) =>
        state.LatestTag is { } tag && SemVersion.TryParse(tag, out var v) && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
            ? new ReleaseInfo(v, tag, state.LatestTitle ?? $"RiotFix {tag}", state.LatestSummary ?? string.Empty, ReleaseUrl(tag), state.LatestPrerelease)
            : null;

    private async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RiotFix", _current.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub API returned {(int)response.StatusCode}.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidDataException("GitHub API response is too large.");
        }

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxResponseBytes + 1];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxResponseBytes)
                {
                    throw new InvalidDataException("GitHub API response is too large.");
                }
            }

            return SelectNewest(ParseReleases(buffer.AsSpan(0, total)));
        }
    }

    private UpdateState SafeLoad()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    private void SafeSave(UpdateState state)
    {
        try
        {
            _store.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Update state could not be saved ({ex.GetType().Name}).");
        }
    }
}

/// <summary>Stores <see cref="UpdateState"/> as a small JSON file (per user, %LocalAppData%\RiotFix).</summary>
public sealed class FileUpdateStateStore(string path) : IUpdateStateStore
{
    public UpdateState Load()
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
        {
            return new UpdateState();
        }

        return JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdateJsonContext.Default.UpdateState) ?? new UpdateState();
    }

    public void Save(UpdateState state)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, UpdateJsonContext.Default.UpdateState));
        File.Move(temp, path, overwrite: true);
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }
}

[JsonSourceGenerationOptions(MaxDepth = 16)]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext
{
}
