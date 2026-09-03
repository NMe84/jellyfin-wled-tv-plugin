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

    /// <summary>Master brightness sent to WLED (0–255).</summary>
    public int Brightness { get; set; } = 128;

    /// <summary>
    /// Milliseconds to shift the LED output so it lines up with the picture on
    /// screen.  Positive pushes the LEDs later (compensating for TV display lag);
    /// negative pulls them earlier (the server decodes further ahead).  Tune per
    /// TV/picture-mode.  0 = no shift.  Range −5000…5000.
    /// </summary>
    public int DisplayLatencyMs { get; set; } = 0;

    /// <summary>Whether the plugin is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Jellyfin device ID whose playback drives the LED strip.  The server samples
    /// only this device's video, so the extra decode is not incurred for anyone
    /// else.  Empty string means "any device".
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// When true, colour updates are split into 54-LED batches to stay within the
    /// ArduinoJson buffer limit on ESP8266 devices.  Disable on ESP32 controllers
    /// (and other devices with ample heap) to send all LEDs in a single message.
    /// </summary>
    public bool BatchUpdates { get; set; } = true;

    /// <summary>
    /// When true, detect horizontal black bars (letterboxing) and map the top and
    /// bottom LEDs to the actual content instead of the full screen.
    /// </summary>
    public bool DetectLetterbox { get; set; } = true;

    /// <summary>
    /// When true, detect vertical black bars (pillarboxing) and map the left and
    /// right LEDs to the actual content instead of the full screen.
    /// </summary>
    public bool DetectPillarbox { get; set; } = true;
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
