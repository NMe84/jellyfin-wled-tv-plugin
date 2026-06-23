using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WledTv.Controllers;

[ApiController]
[Route("WledTv")]
[Produces("application/json")]
public class WledTvController : ControllerBase
{
    private readonly ILogger<WledTvController> _logger;

    public WledTvController(ILogger<WledTvController> logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ── Config endpoint (read by the injected script) ─────────────────────────

    /// <summary>
    /// Returns the configuration the client-side script needs.
    /// The script uses wledWsUrl to open a WebSocket directly to WLED.
    /// </summary>
    [HttpGet("config")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetConfig() =>
        Ok(new
        {
            enabled            = Config.Enabled,
            wledWsUrl          = Config.WledWsUrl,
            horizontalLedCount = Config.HorizontalLedCount,
            verticalLedCount   = Config.VerticalLedCount,
            loopStart          = (int)Config.LoopStart,
            direction          = (int)Config.Direction,
            sampleDepth        = Config.SampleDepth,
            updateIntervalMs   = Config.UpdateIntervalMs,
            brightness         = Config.Brightness,
            deviceId           = Config.DeviceId,
            captureMethod      = Config.CaptureMethod,
            mockLogging        = Config.MockLogging,
            batchUpdates       = Config.BatchUpdates
        });

    // ── Admin settings endpoints (used by the config page) ───────────────────

    [HttpGet("settings")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetSettings() =>
        Ok(new
        {
            enabled            = Config.Enabled,
            wledWsUrl          = Config.WledWsUrl,
            horizontalLedCount = Config.HorizontalLedCount,
            verticalLedCount   = Config.VerticalLedCount,
            loopStart          = (int)Config.LoopStart,
            direction          = (int)Config.Direction,
            sampleDepth        = Config.SampleDepth,
            updateIntervalMs   = Config.UpdateIntervalMs,
            brightness         = Config.Brightness,
            deviceId           = Config.DeviceId,
            captureMethod      = Config.CaptureMethod,
            mockLogging        = Config.MockLogging,
            batchUpdates       = Config.BatchUpdates
        });

    [HttpPost("settings")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SaveSettings([FromBody] SettingsPayload s)
    {
        var cfg = Plugin.Instance!.Configuration;
        cfg.Enabled            = s.Enabled;
        cfg.WledWsUrl          = s.WledWsUrl?.Trim() ?? cfg.WledWsUrl;
        cfg.HorizontalLedCount = Math.Max(1, s.HorizontalLedCount);
        cfg.VerticalLedCount   = Math.Max(1, s.VerticalLedCount);
        cfg.LoopStart          = (LedLoopStart)Math.Clamp(s.LoopStart, 0, 2);
        cfg.Direction          = (LedLoopDirection)Math.Clamp(s.Direction, 0, 1);
        cfg.SampleDepth        = Math.Clamp(s.SampleDepth, 0.01, 0.5);
        cfg.UpdateIntervalMs   = Math.Max(40, s.UpdateIntervalMs);
        cfg.Brightness         = Math.Clamp(s.Brightness, 0, 255);
        cfg.DeviceId           = s.DeviceId?.Trim() ?? string.Empty;
        cfg.CaptureMethod      = Math.Clamp(s.CaptureMethod, 0, 1);
        cfg.MockLogging        = s.MockLogging;
        cfg.BatchUpdates       = s.BatchUpdates;
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    // ── Connectivity test ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts a WebSocket handshake from the server to the configured URL.
    /// Confirms the WLED device is reachable on the network.
    /// </summary>
    [HttpGet("test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestConnection()
    {
        try
        {
            using var ws  = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(Config.WledWsUrl), cts.Token).ConfigureAwait(false);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None)
                    .ConfigureAwait(false);
            return Ok(new { success = true, body = "WebSocket connection successful." });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, body = ex.Message });
        }
    }
}

public class SettingsPayload
{
    public bool   Enabled            { get; set; } = true;
    public string WledWsUrl          { get; set; } = string.Empty;
    public int    HorizontalLedCount { get; set; } = 32;
    public int    VerticalLedCount   { get; set; } = 18;
    public int    LoopStart          { get; set; }
    public int    Direction          { get; set; } = 1;
    public double SampleDepth        { get; set; } = 0.08;
    public int    UpdateIntervalMs   { get; set; } = 100;
    public int    Brightness         { get; set; } = 128;
    public string DeviceId           { get; set; } = string.Empty;
    public int    CaptureMethod      { get; set; } = 0;
    public bool   MockLogging        { get; set; } = false;
    public bool   BatchUpdates       { get; set; } = true;
}