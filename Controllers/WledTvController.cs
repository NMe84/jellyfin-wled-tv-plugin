using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.WledTv.Controllers;

[ApiController]
[Route("WledTv")]
[Produces("application/json")]
public class WledTvController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WledTvController> _logger;

    public WledTvController(IHttpClientFactory httpClientFactory, ILogger<WledTvController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ── Config endpoint (read by the injected script) ─────────────────────────

    /// <summary>
    /// Returns the subset of plugin configuration the client-side script needs.
    /// Accessible to any authenticated user since the script runs in every user session.
    /// </summary>
    [HttpGet("config")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetConfig() =>
        Ok(new
        {
            enabled             = Config.Enabled,
            horizontalLedCount  = Config.HorizontalLedCount,
            verticalLedCount    = Config.VerticalLedCount,
            loopStart           = (int)Config.LoopStart,
            sampleDepth         = Config.SampleDepth,
            updateIntervalMs    = Config.UpdateIntervalMs,
            brightness          = Config.Brightness
        });

    // ── LED proxy endpoint ────────────────────────────────────────────────────

    /// <summary>
    /// Accepts an array of per-LED RGB values from the client-side script and
    /// forwards them to the configured WLED controller.
    /// Proxying through the server avoids any CORS restrictions on the WLED device.
    /// </summary>
    [HttpPost("leds")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> PostLeds([FromBody] LedPayload payload)
    {
        if (!Config.Enabled)
            return NoContent();

        var wledState = new JObject
        {
            ["on"]  = true,
            ["bri"] = payload.Brightness > 0 ? payload.Brightness : Config.Brightness,
            ["seg"] = new JArray(new JObject { ["i"] = new JArray(payload.Leds) })
        };

        try
        {
            var client  = _httpClientFactory.CreateClient();
            var content = new StringContent(wledState.ToString(Formatting.None), Encoding.UTF8, "application/json");
            var url     = Config.WledUrl.TrimEnd('/') + "/json/state";

            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("WledTv: WLED returned {Status} for POST {Url}", (int)response.StatusCode, url);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WledTv: failed to reach WLED at {Url}", Config.WledUrl);
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        return NoContent();
    }

    // ── Admin: test connection ────────────────────────────────────────────────

    [HttpGet("test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestConnection()
    {
        try
        {
            var client   = _httpClientFactory.CreateClient();
            var url      = Config.WledUrl.TrimEnd('/') + "/json/info";
            var response = await client.GetAsync(url).ConfigureAwait(false);
            var body     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return Ok(new { success = response.IsSuccessStatusCode, status = (int)response.StatusCode, body });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, status = 0, body = ex.Message });
        }
    }
}

/// <summary>Payload posted by the client-side script containing per-LED RGB data.</summary>
public class LedPayload
{
    /// <summary>
    /// Flat array of RGB triplets: [R0,G0,B0, R1,G1,B1, …].
    /// Length must equal 3 × (2×H + 2×V).
    /// </summary>
    public int[] Leds { get; set; } = Array.Empty<int>();

    /// <summary>Optional brightness override (0 = use plugin default).</summary>
    public int Brightness { get; set; }
}