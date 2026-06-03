using Jellyfin.Plugin.WledTv.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WledTv;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<LedScriptService>();
        // No AddHttpClient — colour stream now goes browser→WLED directly via WebSocket.
        // The server only needs a plain HttpClient for the WebSocket connectivity test.
        serviceCollection.AddHttpClient();
    }
}