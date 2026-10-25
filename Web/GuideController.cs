using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.Configuration;
using Jellyfin.Plugin.XmltvCleaner.Guides;
using Jellyfin.Plugin.XmltvCleaner.Providers;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XmltvCleaner.Web;

/// <summary>
/// The guide addresses Jellyfin's XMLTV guide providers are pointed at (one per source), plus
/// the settings page's admin-only fetch / status / tuners / guide-provider surface. Answers
/// under JellyDvrGuides/ and, so providers set up before the rename keep working, XmltvCleaner/.
/// </summary>
[ApiController]
[Route("XmltvCleaner")]
[Route("JellyDvrGuides")]
public sealed class GuideController : ControllerBase
{
    private readonly GuideService _guideService;
    private readonly ProviderSync _providers;
    private readonly INetworkManager _network;

    public GuideController(GuideService guideService, ProviderSync providers, INetworkManager network)
    {
        _guideService = guideService;
        _providers = providers;
        _network = network;
    }

    /// <summary>
    /// Anonymous: Jellyfin's XMLTV provider downloads the guide with a plain HttpClient and
    /// sends no credentials. The source's address comes only from plugin config, so this can't
    /// be used to proxy arbitrary URLs, and MinRefetchMinutes stops it from hammering the
    /// source.
    /// </summary>
    [HttpGet("guide/{id}.xml")]
    [AllowAnonymous]
    public Task<IActionResult> GetGuide([FromRoute] string id, CancellationToken cancellationToken) =>
        Serve(GuideService.Find(Config(), id), id, cancellationToken);

    /// <summary>1.0's address: the source moved from its guide URL ("main"), else the first.</summary>
    [HttpGet("guide.xml")]
    [AllowAnonymous]
    public Task<IActionResult> GetFirstGuide(CancellationToken cancellationToken) =>
        Serve(LegacySource(Config(), 1), "1", cancellationToken);

    /// <summary>The 1.1 preview's "More guides" addresses: guide2.xml is the source moved as "guide2"…</summary>
    [HttpGet("guide{feed:int:min(2)}.xml")]
    [AllowAnonymous]
    public Task<IActionResult> GetNumberedGuide([FromRoute] int feed, CancellationToken cancellationToken) =>
        Serve(LegacySource(Config(), feed), feed.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>
    /// The source an address from before sources means: the one migration gave that guide's id
    /// (main, guide2…), else the one in that place in the list.
    /// </summary>
    internal static GuideSource? LegacySource(PluginConfiguration cfg, int feed) =>
        GuideService.Find(cfg, feed == 1 ? "main" : "guide" + feed) ?? cfg.Sources.ElementAtOrDefault(feed - 1);

    private async Task<IActionResult> Serve(GuideSource? source, string asked, CancellationToken cancellationToken)
    {
        if (!HomeNetwork.Allows(HttpContext, _network))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "The guides are only served on the home network (JellyDVR Guides settings).");
        }

        if (source is null)
        {
            return NotFound($"There is no guide source \"{asked}\" (JellyDVR Guides settings).");
        }

        try
        {
            var path = await _guideService.GetGuidePathAsync(source.Id, false, cancellationToken, ServerBase(Request)).ConfigureAwait(false);
            return PhysicalFile(path, "application/xml");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>This server's address as the caller reached it, e.g. "http://192.168.1.188:8096/".</summary>
    internal static string ServerBase(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}/";

    public sealed record SourceStatus(string Id, string Address, GuideService.RefreshStatus? Fetch, ProviderSync.ProviderState? Provider);

    /// <summary>Fetch now (settings page): one source, ignoring MinRefetchMinutes.</summary>
    [HttpPost("Refresh/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SourceStatus[]>> Refresh([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (GuideService.Find(Config(), id) is null)
        {
            return NotFound($"There is no guide source \"{id}\".");
        }

        try
        {
            await _guideService.GetGuidePathAsync(id, true, cancellationToken, Base()).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No previous copy to fall back on; its status already carries the error.
        }

        return Ok(Statuses());
    }

    /// <summary>Each source's last fetch (null: not fetched since the server started) and its Jellyfin guide provider.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SourceStatus[]> Status() => Ok(Statuses());

    /// <summary>Jellyfin's tuners, for the source dialog's "for these tuners" boxes.</summary>
    [HttpGet("Tuners")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<ProviderSync.Tuner>> Tuners() => Ok(_providers.Tuners());

    /// <summary>
    /// After the settings page saved the sources: adds, updates or removes the Jellyfin guide
    /// providers the sources ask for. Returns what changed.
    /// </summary>
    [HttpPost("Providers/Apply")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<List<string>>> ApplyProviders()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return Ok(await _providers.ApplyAsync(plugin, Base()).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, "Couldn't update Jellyfin's guide providers: " + ex.Message);
        }
    }

    /// <summary>
    /// The source card's Remove: deletes another Jellyfin guide provider covering a source's
    /// tuners, then queues Refresh Guide.
    /// </summary>
    [HttpPost("Providers/{providerId}/Remove")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SourceStatus[]> RemoveProvider([FromRoute] string providerId)
    {
        if (!_providers.RemoveOther(Config(), providerId))
        {
            return NotFound("That guide provider doesn't cover any guide source's tuners.");
        }

        return Ok(Statuses());
    }

    private SourceStatus[] Statuses()
    {
        var cfg = Config();
        var serverBase = Base();
        var providers = _providers.States(cfg);
        return cfg.Sources
            .Select((s, i) => new SourceStatus(s.Id, ProviderSync.AddressOf(serverBase, s), _guideService.StatusOf(s.Id), providers.ElementAtOrDefault(i)))
            .ToArray();
    }

    private string Base() => _providers.ServerBase(ServerBase(Request));

    private static PluginConfiguration Config() => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
