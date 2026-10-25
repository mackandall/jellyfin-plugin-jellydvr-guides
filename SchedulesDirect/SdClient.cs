using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyDvrGuides.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDvrGuides.SchedulesDirect;

/// <summary>
/// Talks to the Schedules Direct JSON API (version 20141201), politely. Their servers lock
/// out clients that ask too often: 100 sign-ins a day (500 blocks the address for two days),
/// a 15-minute lockout after failed sign-ins, and a daily image cap. Jellyfin's own Schedules
/// Direct provider signs in and downloads everything on every guide refresh, which is what got
/// the account locked out. So here: one token kept for its whole 24 hours, and any refusal
/// that means "stop" stops every call until it's over (<see cref="BlockedUntil"/>).
/// </summary>
public sealed class SdClient
{
    public const string Api = "https://json.schedulesdirect.org/20141201/";

    // Their terms ask for the software and its version in the user agent.
    private static readonly string UserAgent = "JellyDVR-Guides/" + typeof(SdClient).Assembly.GetName().Version?.ToString(3);

    // Its own client: guide downloads are big and Schedules Direct compresses them.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        // Their server timeout is 10 minutes; a schedule batch can take a while.
        Timeout = TimeSpan.FromMinutes(11)
    };

    private readonly ILogger<SdClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;
    private string? _tokenFor;

    public SdClient(ILogger<SdClient> logger)
    {
        _logger = logger;
    }

    /// <summary>No calls before this (a lockout, a daily limit, the service offline).</summary>
    public DateTime BlockedUntil { get; private set; }

    public string? BlockedWhy { get; private set; }

    /// <summary>Images only: the daily image cap was reached; none before this (00:00 UTC).</summary>
    public DateTime ImagesBlockedUntil { get; private set; }

    public static string HashPassword(string password) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    /// <summary>Signs in once to check the account (the settings page); forgets any old token.</summary>
    public async Task<string> CheckAccountAsync(string username, string passwordHash, CancellationToken ct)
    {
        _token = null;
        await TokenAsync(username, passwordHash, ct).ConfigureAwait(false);
        return _token!;
    }

    public Task<JsonNode> GetAsync(PluginConfiguration cfg, string path, CancellationToken ct) =>
        SendAsync(cfg, HttpMethod.Get, path, null, ct);

    public Task<JsonNode> PostAsync(PluginConfiguration cfg, string path, JsonNode body, CancellationToken ct) =>
        SendAsync(cfg, HttpMethod.Post, path, body, ct);

    /// <summary>
    /// An image's bytes (following Schedules Direct's redirect to its storage), or null when it
    /// doesn't exist. Throws when images are capped for today.
    /// </summary>
    public async Task<(byte[] Bytes, string Type)?> GetImageAsync(PluginConfiguration cfg, string uri, CancellationToken ct)
    {
        if (DateTime.UtcNow < ImagesBlockedUntil)
        {
            throw new SdException(5002, "Schedules Direct's daily image limit was reached; images resume at 00:00 UTC.");
        }

        var url = uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? uri : Api + "image/" + uri;
        var needsToken = url.StartsWith(Api, StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (needsToken)
        {
            CheckBlocked();
            request.Headers.Add("token", await TokenAsync(cfg.SdUsername, cfg.SdPasswordHash, ct).ConfigureAwait(false));
        }

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.SeeOther or HttpStatusCode.Found or HttpStatusCode.MovedPermanently && response.Headers.Location is { } to)
        {
            // The storage link is good for two minutes: fetched now, never kept.
            using var stored = await Http.GetAsync(to, ct).ConfigureAwait(false);
            return stored.IsSuccessStatusCode ? await ReadImage(stored, ct).ConfigureAwait(false) : null;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // Errors come back as JSON with HTTP 200: 5002/5003 = today's image cap.
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var code = node?["code"]?.GetValue<int>() ?? 0;
            if (code is 5002 or 5003)
            {
                ImagesBlockedUntil = DateTime.UtcNow.Date.AddDays(1);
                _logger.LogWarning("Schedules Direct: daily image limit reached; no image downloads until 00:00 UTC");
            }

            if (code == 5000)
            {
                return null;
            }

            throw new SdException(code, node?["message"]?.ToString() ?? "image request refused");
        }

        return response.IsSuccessStatusCode ? await ReadImage(response, ct).ConfigureAwait(false) : null;
    }

    private static async Task<(byte[] Bytes, string Type)> ReadImage(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");

    private async Task<JsonNode> SendAsync(PluginConfiguration cfg, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            CheckBlocked();
            var token = await TokenAsync(cfg.SdUsername, cfg.SdPasswordHash, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, Api + path);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Add("token", token);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new SdException(0, $"unexpected reply from Schedules Direct (HTTP {(int)response.StatusCode})");
            }

            // Lists come back bare; an object with a non-zero code is a refusal.
            if (node is JsonObject o && o["code"] is { } c && c.GetValue<int>() is var code && code != 0)
            {
                if (code == 4006 && attempt == 0)
                {
                    // Token expired early: one new one, once.
                    _token = null;
                    continue;
                }

                throw Refused(code, o["message"]?.ToString());
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SdException(0, $"Schedules Direct answered HTTP {(int)response.StatusCode}");
            }

            return node ?? throw new SdException(0, "empty reply from Schedules Direct");
        }
    }

    private async Task<string> TokenAsync(string username, string passwordHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new SdException(0, "No Schedules Direct account in the plugin settings.");
        }

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _tokenFor == username && DateTime.UtcNow < _tokenExpiresUtc.AddMinutes(-10))
            {
                return _token;
            }

            CheckBlocked();
            using var request = new HttpRequestMessage(HttpMethod.Post, Api + "token");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Content = new StringContent(
                new JsonObject { ["username"] = username, ["password"] = passwordHash }.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var code = node?["code"]?.GetValue<int>() ?? -1;
            if (code != 0 || node?["token"]?.ToString() is not { Length: > 0 } token)
            {
                throw Refused(code, node?["message"]?.ToString());
            }

            _token = token;
            _tokenFor = username;
            _tokenExpiresUtc = node["tokenExpires"]?.GetValue<long>() is long expires and > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime
                : DateTime.UtcNow.AddHours(23);
            _logger.LogInformation("Schedules Direct: signed in as {User} (token until {Expires:u})", username, _tokenExpiresUtc);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void CheckBlocked()
    {
        if (DateTime.UtcNow < BlockedUntil)
        {
            throw new SdException(0, $"Not asking Schedules Direct until {BlockedUntil.ToLocalTime():g}: {BlockedWhy}");
        }
    }

    // A refusal; the ones that mean "stop for a while" stop every call for that long.
    private SdException Refused(int code, string? message)
    {
        var wait = code switch
        {
            3000 => TimeSpan.FromMinutes(30), // service offline
            3001 => TimeSpan.FromMinutes(5), // busy
            4001 or 4005 or 4007 or 4008 => TimeSpan.FromHours(24), // account expired, disabled, inactive
            4003 or 4002 => TimeSpan.FromMinutes(30), // wrong account details: don't keep trying them
            4004 => TimeSpan.FromMinutes(16), // locked out after failed sign-ins
            4009 or 4010 or 1050 => DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow + TimeSpan.FromHours(1), // daily limits
            _ => TimeSpan.Zero
        };
        var text = $"Schedules Direct refused ({code}): {message ?? "no message"}";
        if (wait > TimeSpan.Zero)
        {
            BlockedUntil = DateTime.UtcNow + wait;
            BlockedWhy = text;
            _token = null;
            _logger.LogWarning("{Text}; no more calls until {Until:u}", text, BlockedUntil);
        }

        return new SdException(code, text);
    }

    /// <summary>Lets the settings page try again after fixing the account details.</summary>
    public void ClearAccountBlock()
    {
        if (BlockedWhy?.Contains("(4003)", StringComparison.Ordinal) == true || BlockedWhy?.Contains("(4002)", StringComparison.Ordinal) == true)
        {
            BlockedUntil = DateTime.MinValue;
            BlockedWhy = null;
        }
    }
}

/// <summary>Schedules Direct said no (Code: their error code, 0 when it wasn't theirs).</summary>
public sealed class SdException : Exception
{
    public SdException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
