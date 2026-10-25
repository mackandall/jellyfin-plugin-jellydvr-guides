using Jellyfin.Plugin.XmltvCleaner.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XmltvCleaner;

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
        serviceCollection.AddSingleton<GuideService>();

        // Jellyfin builds its MVC ApplicationPartManager from its own assembly before any
        // plugin loads and never re-scans, so a plugin's controllers are silently absent
        // unless it adds itself explicitly.
        serviceCollection.AddControllers().AddApplicationPart(typeof(GuideController).Assembly);
    }
}
