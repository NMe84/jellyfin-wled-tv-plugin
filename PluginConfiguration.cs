using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WledTv;

/// <summary>
/// Persisted configuration for the WLED TV plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Base URL of the WLED HTTP JSON API, e.g. http://192.168.1.50</summary>
    public string WledUrl { get; set; } = "http://wled.local";

    /// <summary>Number of LEDs along the horizontal edges (top and bottom).</summary>
    public int HorizontalLedCount { get; set; } = 32;

    /// <summary>Number of LEDs along the vertical edges (left and right).</summary>
    public int VerticalLedCount { get; set; } = 18;

    /// <summary>Physical LED density of the strip in LEDs per metre.</summary>
    public int LedsPerMeter { get; set; } = 60;

    /// <summary>
    /// Where LED #0 sits on the physical strip.
    /// The strip always runs clockwise when viewed from the front.
    /// </summary>
    public LedLoopStart LoopStart { get; set; } = LedLoopStart.BottomCenter;

    /// <summary>How deep into the frame (as a fraction 0–1) to sample pixels from each edge.</summary>
    public double SampleDepth { get; set; } = 0.08;

    /// <summary>Milliseconds between successive colour updates (100 = 10 fps).</summary>
    public int UpdateIntervalMs { get; set; } = 100;

    /// <summary>Master brightness sent to WLED (0–255).</summary>
    public int Brightness { get; set; } = 128;

    /// <summary>Whether the plugin is active.</summary>
    public bool Enabled { get; set; } = true;
}

public enum LedLoopStart
{
    /// <summary>Strip starts at the centre of the bottom edge and runs clockwise.</summary>
    BottomCenter = 0,

    /// <summary>Strip starts at the bottom-left corner and runs clockwise.</summary>
    BottomLeft = 1,

    /// <summary>Strip starts at the bottom-right corner and runs clockwise.</summary>
    BottomRight = 2,
}