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
    public async Task<IActionResult> GetGuide(CancellationToken cancellationToken)
    {
        // Home network only (setting): the server itself and the networks Jellyfin counts as LAN.
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (Plugin.Instance?.Configuration.LocalNetworkOnly != false
            && (remote is null || !(System.Net.IPAddress.IsLoopback(remote) || _network.IsInLocalNetwork(remote))))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "The cleaned guide is only served on the home network (XmltvCleaner settings).");
        }

        try
        {
            var path = await _guideService.GetGuidePathAsync(false, cancellationToken).ConfigureAwait(false);
            return PhysicalFile(path, "application/xml");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    [HttpPost("Refresh")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<GuideService.RefreshStatus?>> Refresh(CancellationToken cancellationToken)
    {
        try
        {
            await _guideService.GetGuidePathAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No previous copy to fall back on; LastStatus already carries the error.
        }

        return Ok(_guideService.LastStatus);
    }

    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<GuideService.RefreshStatus?> Status()
    {
        return Ok(_guideService.LastStatus);
    }
}
