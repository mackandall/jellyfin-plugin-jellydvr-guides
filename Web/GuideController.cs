using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XmltvCleaner.Web;

/// <summary>
/// The cleaned guide endpoint Jellyfin's XMLTV listings provider is pointed at, plus an
/// admin-only refresh/status surface for the config page.
/// </summary>
[ApiController]
[Route("XmltvCleaner")]
public sealed class GuideController : ControllerBase
{
    private readonly GuideService _guideService;
    private readonly INetworkManager _network;

    public GuideController(GuideService guideService, INetworkManager network)
    {
        _guideService = guideService;
        _network = network;
    }

    /// <summary>
    /// Anonymous: Jellyfin's XMLTV provider downloads the guide with a plain HttpClient and
    /// sends no credentials. The upstream URL comes only from plugin config, so this can't
    /// be used to proxy arbitrary URLs, and MinRefetchMinutes stops it from hammering the
    /// upstream.
    /// </summary>
    [HttpGet("guide.xml")]
    [AllowAnonymous]
    public Task<IActionResult> GetGuide(CancellationToken cancellationToken) => Serve(1, cancellationToken);

    /// <summary>An extra guide (settings: "More guides"): guide2.xml is the first, guide3.xml the next…</summary>
    [HttpGet("guide{feed:int:min(2)}.xml")]
    [AllowAnonymous]
    public Task<IActionResult> GetExtraGuide([FromRoute] int feed, CancellationToken cancellationToken) => Serve(feed, cancellationToken);

    private async Task<IActionResult> Serve(int feed, CancellationToken cancellationToken)
    {
        // Home network only (setting): the server itself and the networks Jellyfin counts as LAN.
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (Plugin.Instance?.Configuration.LocalNetworkOnly != false
            && (remote is null || !(System.Net.IPAddress.IsLoopback(remote) || _network.IsInLocalNetwork(remote))))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "The cleaned guide is only served on the home network (XmltvCleaner settings).");
        }

        if (GuideService.UpstreamOf(Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration(), feed) is null)
        {
            return NotFound($"There is no guide {feed} (XmltvCleaner settings).");
        }

        try
        {
            var path = await _guideService.GetGuidePathAsync(feed, false, cancellationToken, ServerBase(Request)).ConfigureAwait(false);
            return PhysicalFile(path, "application/xml");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>This server's address as the caller reached it, e.g. "http://192.168.1.188:8096/".</summary>
    internal static string ServerBase(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}/";

    [HttpPost("Refresh")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<GuideService.RefreshStatus?[]>> Refresh(CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        for (var feed = 1; feed <= GuideService.FeedCount(cfg); feed++)
        {
            try
            {
                await _guideService.GetGuidePathAsync(feed, true, cancellationToken, ServerBase(Request)).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No previous copy to fall back on; its status already carries the error.
            }
        }

        return Ok(Statuses(cfg));
    }

    /// <summary>Each guide's last refresh, the main one first (null: not fetched since the server started).</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<GuideService.RefreshStatus?[]> Status()
    {
        return Ok(Statuses(Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration()));
    }

    private GuideService.RefreshStatus?[] Statuses(Configuration.PluginConfiguration cfg)
    {
        var all = new GuideService.RefreshStatus?[GuideService.FeedCount(cfg)];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = _guideService.StatusOf(i + 1);
        }

        return all;
    }
}
