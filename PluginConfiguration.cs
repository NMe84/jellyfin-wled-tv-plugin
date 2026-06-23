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

    /// <summary>
    /// Jellyfin device ID that should run the edge-lighting script.
    /// Empty string means "all devices" (the original behaviour).
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// How the plugin captures video frames from the browser.
    /// 0 = Canvas 2D (default). 1 = WebGL (for hardware-overlay platforms like WebOS).
    /// </summary>
    public int CaptureMethod { get; set; } = 0;

    /// <summary>
    /// When true, send diagnostic log messages to wled-ambilight-mock via WebSocket.
    /// Has no effect when connected to a real WLED device.
    /// </summary>
    public bool MockLogging { get; set; } = false;

    /// <summary>
    /// When true, colour updates are split into 54-LED batches to stay within the
    /// ArduinoJson buffer limit on ESP8266 devices.  Disable on ESP32 controllers
    /// (and other devices with ample heap) to send all LEDs in a single message.
    /// </summary>
    public bool BatchUpdates { get; set; } = true;
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