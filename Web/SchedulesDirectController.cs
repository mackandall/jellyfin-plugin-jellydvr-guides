using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.XmltvCleaner.SchedulesDirect;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XmltvCleaner.Web;

/// <summary>
/// Schedules Direct: the settings page's account check, lineups, Update now and status
/// (admins only), and the programme pictures the guides link to (no sign-in, like the guides).
/// </summary>
[ApiController]
[Route("XmltvCleaner")]
[Route("JellyDvrGuides")]
public sealed class SchedulesDirectController : ControllerBase
{
    private readonly SdClient _client;
    private readonly SdUpdater _updater;
    private readonly SdImages _images;
    private readonly INetworkManager _network;

    public SchedulesDirectController(SdClient client, SdUpdater updater, SdImages images, INetworkManager network)
    {
        _client = client;
        _updater = updater;
        _images = images;
        _network = network;
    }

    public sealed class AccountRequest
    {
        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Signs in once with these details and, if they work, keeps the account name and the
    /// password's hash (Schedules Direct's own form) in the settings. The password itself is
    /// never stored.
    /// </summary>
    [HttpPost("SchedulesDirect/Account")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<string>> SetAccount([FromBody] AccountRequest account, CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrEmpty(account.Password))
        {
            return BadRequest("Enter the Schedules Direct account name and password.");
        }

        var hash = SdClient.HashPassword(account.Password);
        _client.ClearAccountBlock();
        try
        {
            await _client.CheckAccountAsync(account.Username.Trim(), hash, ct).ConfigureAwait(false);
        }
        catch (SdException ex)
        {
            return BadRequest(ex.Message);
        }

        plugin.Configuration.SdUsername = account.Username.Trim();
        plugin.Configuration.SdPasswordHash = hash;
        plugin.SaveConfiguration();
        return Ok("Signed in to Schedules Direct as " + account.Username.Trim() + ".");
    }

    public sealed record SdStatus(SdUpdater.UpdateStatus? Update, object[] Lineups, string? Blocked, string? ImagesBlocked, string Username);

    [HttpGet("SchedulesDirect/Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<SdStatus> Status()
    {
        var lineups = (_updater.CachedLineups() ?? new System.Text.Json.Nodes.JsonArray())
            .Select(l => (object)new
            {
                Lineup = l?["lineup"]?.ToString(),
                Name = l?["name"]?.ToString(),
                Transport = l?["transport"]?.ToString(),
                Location = l?["location"]?.ToString()
            })
            .ToArray();
        var now = DateTime.UtcNow;
        return Ok(new SdStatus(
            _updater.LastStatus,
            lineups,
            _client.BlockedUntil > now ? $"until {_client.BlockedUntil.ToLocalTime():g}: {_client.BlockedWhy}" : null,
            _client.ImagesBlockedUntil > now ? $"daily image limit reached; images resume {_client.ImagesBlockedUntil.ToLocalTime():g}" : null,
            Plugin.Instance?.Configuration.SdUsername ?? string.Empty));
    }

    /// <summary>Update now (the settings page): ignores the once-a-day rule, not the lockouts.</summary>
    [HttpPost("SchedulesDirect/Update")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<SdStatus>> Update(CancellationToken ct)
    {
        try
        {
            await _updater.UpdateAsync(true, null, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The status carries the error.
        }

        return Status();
    }

    /// <summary>A programme picture the guide links to; fetched from Schedules Direct the first time only.</summary>
    [HttpGet("image/{key}.jpg")]
    [AllowAnonymous]
    public async Task<IActionResult> Image([FromRoute] string key, [FromQuery] string p, CancellationToken ct)
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (Plugin.Instance?.Configuration.LocalNetworkOnly != false
            && (remote is null || !(System.Net.IPAddress.IsLoopback(remote) || _network.IsInLocalNetwork(remote))))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var path = await _images.GetAsync(key, p ?? string.Empty, ct).ConfigureAwait(false);
        return path is null ? NotFound() : PhysicalFile(path, "image/jpeg");
    }
}
