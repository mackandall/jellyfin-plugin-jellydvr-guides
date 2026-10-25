using System.Net;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyDvrGuides.Web;

/// <summary>
/// The "only on the home network" setting for the addresses that need no sign-in (guides and
/// programme pictures): the server itself and the networks Jellyfin counts as local
/// (Dashboard › Networking).
/// </summary>
internal static class HomeNetwork
{
    /// <summary>True when the setting is off or the caller is on the home network.</summary>
    public static bool Allows(HttpContext context, INetworkManager network)
    {
        if (Plugin.Instance?.Configuration.LocalNetworkOnly == false)
        {
            return true;
        }

        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && (IPAddress.IsLoopback(remote) || network.IsInLocalNetwork(remote));
    }
}
