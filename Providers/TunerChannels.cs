using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDvrGuides.Providers;

/// <summary>The channel numbers Jellyfin's tuners carry, from Jellyfin's cached channel lists.</summary>
public sealed class TunerChannels
{
    private readonly ITunerHostManager _tunerHosts;
    private readonly ILogger<TunerChannels> _logger;

    public TunerChannels(ITunerHostManager tunerHosts, ILogger<TunerChannels> logger)
    {
        _tunerHosts = tunerHosts;
        _logger = logger;
    }

    /// <summary>A channel number as tuners and lineups both can write it: "004-1" and "4.1" are 4.1, "0202" is 202.</summary>
    public static string Normalize(string number) =>
        string.Join('.', number.Trim().Replace('-', '.').Replace('_', '.').Split('.').Select(p => p.TrimStart('0') is { Length: > 0 } t ? t : "0"));

    /// <summary>
    /// The (normalized) channel numbers these tuners have, or null when there are no tuners or
    /// none of them listed a channel. <paramref name="forWhat"/> names the caller in the log.
    /// </summary>
    public async Task<ISet<string>?> NumbersAsync(IEnumerable<string> tunerIds, string forWhat, CancellationToken ct)
    {
        var tuners = new HashSet<string>(tunerIds, StringComparer.OrdinalIgnoreCase);
        if (tuners.Count == 0)
        {
            return null;
        }

        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in _tunerHosts.TunerHosts)
        {
            try
            {
                foreach (var channel in await host.GetChannels(true, ct).ConfigureAwait(false))
                {
                    if (channel.TunerHostId is not null && tuners.Contains(channel.TunerHostId) && !string.IsNullOrWhiteSpace(channel.Number))
                    {
                        numbers.Add(Normalize(channel.Number));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Couldn't list the channels of {Type} tuners for {What}", host.Type, forWhat);
            }
        }

        if (numbers.Count == 0)
        {
            _logger.LogWarning("{What}: its tuners listed no channels", forWhat);
            return null;
        }

        return numbers;
    }
}
