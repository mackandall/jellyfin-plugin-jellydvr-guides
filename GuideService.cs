using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XmltvCleaner;

/// <summary>
/// Fetches the upstream guide, runs it through <see cref="GuideCleaner"/>, and keeps the
/// last good cleaned copy on disk. Feed 1 is the main upstream (guide.xml); feeds 2, 3, …
/// are the extra upstreams (guide2.xml, guide3.xml, …), each with its own copy and status.
///
/// Serving the cleaned guide on demand (rather than writing a file on a separate
/// schedule) means Jellyfin's own "Refresh Guide" task always gets data as fresh as the
/// upstream, with no second schedule to keep in step with it.
///
/// Why the last-good fallback matters: Jellyfin's XmlTvListingsProvider never checks the
/// HTTP status of an XMLTV URL (confirmed by decompiling Jellyfin.LiveTv 12.0.0) — it
/// caches whatever body comes back as the guide file. So a transient upstream failure is
/// answered with the previous cleaned copy, never an error page, whenever one exists.
/// </summary>
public sealed class GuideService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GuideService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _directory;
    private readonly Dictionary<int, RefreshStatus> _statuses = new();

    public GuideService(IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths, ILogger<GuideService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Under the server's data dir, not the plugin's own (versioned) install folder, so
        // the last good copy survives a plugin upgrade.
        _directory = Path.Combine(applicationPaths.DataPath, "xmltv-cleaner");
    }

    /// <summary>The upstream URL of a feed, or null when there is no such feed.</summary>
    public static string? UpstreamOf(PluginConfiguration cfg, int feed) =>
        feed == 1 ? cfg.UpstreamUrl
        : feed >= 2 && feed - 2 < cfg.ExtraUpstreamUrls.Length ? cfg.ExtraUpstreamUrls[feed - 2]
        : null;

    /// <summary>How many feeds are set up (the main one counts even while its URL is empty).</summary>
    public static int FeedCount(PluginConfiguration cfg) => 1 + cfg.ExtraUpstreamUrls.Length;

    private string PathOf(int feed) => Path.Combine(_directory, feed == 1 ? "guide.xml" : $"guide{feed}.xml");

    public sealed record RefreshStatus(
        DateTime? FetchedUtc,
        int Programmes,
        int TitlesCleaned,
        int ProgramIdsAdded,
        IReadOnlyList<string> Samples,
        long DurationMs,
        string? LastError,
        DateTime? LastErrorUtc);

    public RefreshStatus? StatusOf(int feed)
    {
        lock (_statuses)
        {
            return _statuses.GetValueOrDefault(feed);
        }
    }

    private void SetStatus(int feed, RefreshStatus status)
    {
        lock (_statuses)
        {
            _statuses[feed] = status;
        }
    }

    /// <summary>
    /// Returns the path of a cleaned guide file to serve, refreshing it from upstream first
    /// unless the current copy is younger than <see cref="PluginConfiguration.MinRefetchMinutes"/>
    /// (or <paramref name="force"/> is set). Throws only when upstream fails AND no previous
    /// cleaned copy exists.
    /// </summary>
    public async Task<string> GetGuidePathAsync(int feed, bool force, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var upstream = UpstreamOf(cfg, feed) ?? throw new InvalidOperationException($"There is no guide {feed} (plugin settings).");
        var guidePath = PathOf(feed);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && File.Exists(guidePath)
                && File.GetLastWriteTimeUtc(guidePath) >= DateTime.UtcNow.AddMinutes(-Math.Max(0, cfg.MinRefetchMinutes)))
            {
                return guidePath;
            }

            try
            {
                await RefreshAsync(feed, upstream, guidePath, cfg, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                SetStatus(feed, (StatusOf(feed) ?? new RefreshStatus(null, 0, 0, 0, Array.Empty<string>(), 0, null, null))
                    with { LastError = ex.Message, LastErrorUtc = DateTime.UtcNow });

                if (File.Exists(guidePath))
                {
                    _logger.LogWarning(ex, "Refreshing XMLTV guide {Feed} from upstream failed; serving the last good cleaned copy from {Time:u}",
                        feed, File.GetLastWriteTimeUtc(guidePath));
                    return guidePath;
                }

                _logger.LogError(ex, "Refreshing XMLTV guide {Feed} from upstream failed and no previous cleaned copy exists", feed);
                throw;
            }

            return guidePath;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RefreshAsync(int feed, string upstream, string guidePath, PluginConfiguration cfg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(upstream))
        {
            throw new InvalidOperationException("No upstream XMLTV URL is configured (plugin settings).");
        }

        Directory.CreateDirectory(_directory);
        var tempPath = guidePath + ".tmp";
        var sw = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, cfg.UpstreamTimeoutSeconds)));

        GuideCleaner.CleanResult result;
        try
        {
            using var response = await _httpClientFactory.CreateClient()
                .GetAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (output.ConfigureAwait(false))
                {
                    // XmlReader over a network stream is synchronous; run it off the
                    // request thread.
                    result = await Task.Run(() => GuideCleaner.Clean(input, output, timeout.Token), timeout.Token)
                        .ConfigureAwait(false);
                }
            }

            // A well-formed but empty guide almost certainly means an upstream hiccup;
            // don't let it replace a good copy.
            if (result.Programmes == 0)
            {
                throw new InvalidDataException("Upstream guide contained no <programme> entries.");
            }

            File.Move(tempPath, guidePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        sw.Stop();
        SetStatus(feed, new RefreshStatus(DateTime.UtcNow, result.Programmes, result.TitlesCleaned, result.ProgramIdsAdded, result.Samples, sw.ElapsedMilliseconds, null, null));
        _logger.LogInformation(
            "Cleaned XMLTV guide {Feed}: {Programmes} programmes, {Cleaned} titles had a duplicate year stripped, {Ids} tms ids copied to dd_progid ({Ms} ms)",
            feed,
            result.Programmes, result.TitlesCleaned, result.ProgramIdsAdded, sw.ElapsedMilliseconds);
    }
}
