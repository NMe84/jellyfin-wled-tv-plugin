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
            brightness         = Config.Brightness,
            displayLatencyMs   = Config.DisplayLatencyMs,
            deviceId           = Config.DeviceId,
            batchUpdates       = Config.BatchUpdates,
            detectLetterbox    = Config.DetectLetterbox,
            detectPillarbox    = Config.DetectPillarbox
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
        cfg.Brightness         = Math.Clamp(s.Brightness, 0, 255);
        cfg.DisplayLatencyMs   = Math.Clamp(s.DisplayLatencyMs, 0, 5000);
        cfg.DeviceId           = s.DeviceId?.Trim() ?? string.Empty;
        cfg.BatchUpdates       = s.BatchUpdates;
        cfg.DetectLetterbox    = s.DetectLetterbox;
        cfg.DetectPillarbox    = s.DetectPillarbox;
        Plugin.Instance!.SaveConfiguration();
        return NoContent();
    }

    // ── Connectivity test ─────────────────────────────────────────────────────

    /// <summary>
    /// Attempts a WebSocket handshake from the SERVER to the given URL (or the
    /// saved one if none is supplied).  This mirrors the runtime path — the server
    /// is what talks to WLED — so it is the meaningful reachability check.
    /// </summary>
    [HttpGet("test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestConnection([FromQuery] string? url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? Config.WledWsUrl : url.Trim();
        try
        {
            using var ws  = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(target), cts.Token).ConfigureAwait(false);
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
    public int    Brightness         { get; set; } = 128;
    public int    DisplayLatencyMs   { get; set; }
    public string DeviceId           { get; set; } = string.Empty;
    public bool   BatchUpdates       { get; set; } = true;
    public bool   DetectLetterbox    { get; set; } = true;
    public bool   DetectPillarbox    { get; set; } = true;
}
