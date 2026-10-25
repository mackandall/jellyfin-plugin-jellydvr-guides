using Jellyfin.Plugin.JellyDvrGuides.Guides;
using Jellyfin.Plugin.JellyDvrGuides.Providers;
using Jellyfin.Plugin.JellyDvrGuides.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyDvrGuides;

/// <summary>
/// Registers plugin services into Jellyfin's DI container at startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SchedulesDirect.SdClient>();
        serviceCollection.AddSingleton<SchedulesDirect.SdUpdater>();
        serviceCollection.AddSingleton<SchedulesDirect.SdImages>();
        serviceCollection.AddSingleton<GuideCopies>();
        serviceCollection.AddSingleton<TunerChannels>();
        serviceCollection.AddSingleton<GuideService>();
        serviceCollection.AddSingleton<ProviderSync>();

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(GuideController).Assembly);
    }
}
