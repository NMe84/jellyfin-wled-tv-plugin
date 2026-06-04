using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WledTv;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override Guid Id => new Guid("a4b5c6d7-e8f9-4a3b-8c7d-e6f5a4b3c2d1");
    public override string Name => "WLED TV";
    public override string Description => "Drive a WLED LED strip from video content playing in Jellyfin.";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override Stream? GetThumbImage()
    {
        var type = GetType();
        return type.Assembly.GetManifestResourceStream($"{type.Namespace}.wledtv.png");
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
        new[]
        {
            new PluginPageInfo
            {
                Name                = "wledtv",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu    = true,
                MenuSection         = "server",
                MenuIcon            = "lightbulb",
                DisplayName         = "WLED TV"
            }
        };
}