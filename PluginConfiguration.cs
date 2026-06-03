using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WledTv;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Full WebSocket DSN of the WLED device, e.g. ws://192.168.1.50/ws</summary>
    public string WledWsUrl { get; set; } = "ws://wled.local/ws";

    /// <summary>Number of LEDs along the horizontal edges (top and bottom).</summary>
    public int HorizontalLedCount { get; set; } = 32;

    /// <summary>Number of LEDs along the vertical edges (left and right).</summary>
    public int VerticalLedCount { get; set; } = 18;

    /// <summary>Where LED #0 sits on the physical strip.</summary>
    public LedLoopStart LoopStart { get; set; } = LedLoopStart.BottomCenter;

    /// <summary>Which way the strip runs from the start point.</summary>
    public LedLoopDirection Direction { get; set; } = LedLoopDirection.CounterClockwise;

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
    BottomCenter = 0,
    BottomLeft   = 1,
    BottomRight  = 2,
}

public enum LedLoopDirection
{
    Clockwise        = 0,
    CounterClockwise = 1,
}