using Jellyfin.Plugin.WledTv.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WledTv;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Server-side edge lighting: decodes the selected device's playback with
        // the bundled ffmpeg, samples the edges, and streams colours to WLED.
        serviceCollection.AddHostedService<WledSamplingService>();
    }
}
