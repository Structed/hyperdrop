using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperDrop.Core.Update;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;

/// <summary>Shape of the release payload, reduced to the fields that matter.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Reads releases from the GitHub REST API.
/// </summary>
/// <remarks>
/// Uses <c>/releases/latest</c> rather than listing every release, because that endpoint already
/// excludes drafts and prereleases. The call is unauthenticated: GitHub allows 60 requests an hour
/// per address, and HyperDrop checks once a day, so asking the user for a token would buy nothing.
/// </remarks>
public sealed class GitHubReleaseSource : IUpdateSource, IDisposable
{
    public const string DefaultRepository = "Structed/hyperdrop";

    /// <summary>
    /// Suffix of the asset that <c>release.yml</c> publishes. Matching on the suffix rather than
    /// the full name keeps this working when the version in the middle changes.
    /// </summary>
    internal const string PackageSuffix = "-win-x64.zip";

    internal const string ChecksumSuffix = ".sha256";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _repository;

    public GitHubReleaseSource(string? repository = null, string? productVersion = null)
        : this(CreateClient(productVersion), repository ?? DefaultRepository, ownsClient: true)
    {
    }

    internal GitHubReleaseSource(HttpClient client, string repository, bool ownsClient = false)
    {
        _client = client;
        _repository = repository;
        _ownsClient = ownsClient;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        GitHubRelease? release;

        try
        {
            var url = $"https://api.github.com/repos/{_repository}/releases/latest";

            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            release = await JsonSerializer
                .DeserializeAsync(stream, ReleaseJsonContext.Default.GitHubRelease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, ex);
        }

        if (release is null || !ReleaseInfo.TryParseTag(release.TagName, out var version))
        {
            return null;
        }

        var package = release.Assets.Find(asset =>
            asset.Name is not null &&
            asset.Name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));

        var checksum = package?.Name is null
            ? null
            : release.Assets.Find(asset =>
                string.Equals(asset.Name, package.Name + ChecksumSuffix, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl));

        return new ReleaseInfo(
            version,
            release.TagName!.Trim(),
            release.HtmlUrl ?? $"https://github.com/{_repository}/releases",
            package?.Name,
            package?.BrowserDownloadUrl,
            checksum?.BrowserDownloadUrl,
            package?.Size ?? 0);
    }

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            return await _client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, ex);
        }
    }

    public async Task DownloadAsync(
        string url,
        Stream destination,
        long expectedLength,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            // ResponseHeadersRead keeps the body out of memory, and takes the download out of
            // HttpClient.Timeout, which would otherwise cap a large package on a slow line.
            using var response = await _client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? expectedLength;
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await CopyWithProgressAsync(source, destination, total, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateFailure.Network, ex);
        }
    }

    internal static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long total,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        var lastReported = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            if (progress is null || total <= 0)
            {
                continue;
            }

            // Whole percent only: a progress bar redrawn per 80 KB chunk costs more than it shows.
            var percent = (int)(copied * 100 / total);
            if (percent != lastReported)
            {
                lastReported = percent;
                progress.Report(Math.Clamp(percent / 100d, 0d, 1d));
            }
        }

        progress?.Report(1d);
    }

    /// <remarks>
    /// The GitHub API rejects requests without a User-Agent, and answers with the older media type
    /// unless the version header is sent.
    /// </remarks>
    internal static HttpClient CreateClient(string? productVersion)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HyperDrop", SanitiseVersion(productVersion)));

        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    /// <summary>
    /// ProductInfoHeaderValue rejects anything that is not a valid token, and an informational
    /// version can carry characters it will not accept.
    /// </summary>
    private static string SanitiseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var cleaned = new string([.. version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')]);
        return cleaned.Length == 0 ? "0.0.0" : cleaned;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
