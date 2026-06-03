using System;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.WledTv.Services;

/// <summary>
/// Registers (and unregisters) the edge-sampling script with the JavaScript Injector plugin.
/// The script runs inside the Jellyfin web client, samples video pixels along screen edges,
/// and sends the colours directly to WLED via WebSocket.
/// </summary>
public class LedScriptService : IHostedService, IDisposable
{
    private const string ScriptId         = "wledtv-edge-leds";
    private const string InjectorAssembly = "Jellyfin.Plugin.JavaScriptInjector";
    private const string InjectorType     = "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";

    private readonly ILogger<LedScriptService> _logger;
    private bool _disposed;

    public LedScriptService(ILogger<LedScriptService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return Task.CompletedTask;

        var payload = new JObject
        {
            ["id"]                     = ScriptId,
            ["name"]                   = "WLED TV – Edge Lighting",
            ["script"]                 = EdgeLightingScript,
            ["enabled"]                = true,
            ["requiresAuthentication"] = true,
            ["pluginId"]               = plugin.Id.ToString(),
            ["pluginName"]             = plugin.Name,
            ["pluginVersion"]          = plugin.Version.ToString()
        };

        try
        {
            var type = ResolveInjectorType();
            if (type is null)
            {
                _logger.LogDebug("WledTv: JavaScript Injector not present — edge-lighting script not registered");
                return Task.CompletedTask;
            }

            var method = type.GetMethod("RegisterScript", BindingFlags.Static | BindingFlags.Public);
            var result = method?.Invoke(null, new object[] { payload });
            if (result is true)
                _logger.LogInformation("WledTv: edge-lighting script registered via JavaScript Injector");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WledTv: failed to register script with JavaScript Injector");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return Task.CompletedTask;

        try
        {
            var type = ResolveInjectorType();
            if (type is null) return Task.CompletedTask;

            var method = type.GetMethod("UnregisterAllScriptsFromPlugin", BindingFlags.Static | BindingFlags.Public);
            method?.Invoke(null, new object[] { plugin.Id.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WledTv: failed to unregister script from JavaScript Injector");
        }

        return Task.CompletedTask;
    }

    private static Type? ResolveInjectorType()
    {
        foreach (var ctx in AssemblyLoadContext.All)
        {
            foreach (var asm in ctx.Assemblies)
            {
                if (asm.FullName?.Contains(InjectorAssembly, StringComparison.Ordinal) == true)
                {
                    var t = asm.GetType(InjectorType);
                    if (t is not null) return t;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed) _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ── Injected script ────────────────────────────────────────────────────────
    //
    // Style notes for this C# verbatim string:
    //   • Only single quotes used in JS to avoid "" escaping.
    //   • No template literals — avoids backtick complications.
    //   • X-Emby-Token header for Jellyfin auth.
    private const string EdgeLightingScript = @"
(function () {
  'use strict';

  // ── State ─────────────────────────────────────────────────────────────────
  var config     = null;   // last fetched config object
  var cfgAt      = 0;      // timestamp of last successful fetch

  var canvas     = null;
  var ctx        = null;

  var running    = false;  // true while the sampling loop is active
  var tickTimer  = null;   // handle returned by setTimeout
  var ledsOn     = false;  // true once we have sent at least one colour frame

  // WebSocket state — deliberately simple: one live socket, one retry timestamp.
  var ws         = null;
  var wsRetryAt  = 0;      // do not attempt (re)connect before this timestamp

  var _logAt     = 0;      // rate-limit helper for the 'wait' diagnostic log

  // ── Config ────────────────────────────────────────────────────────────────

  function baseUrl() {
    return window.ApiClient ? window.ApiClient.serverAddress() : window.location.origin;
  }
  function authToken() {
    return window.ApiClient ? window.ApiClient.accessToken() : '';
  }

  function loadConfig(cb) {
    var now = Date.now();
    if (config && (now - cfgAt) < 30000) { cb(); return; }
    fetch(baseUrl() + '/WledTv/config', { headers: { 'X-Emby-Token': authToken() } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (c) { if (c) { config = c; cfgAt = now; } cb(); })
      .catch(function () { cb(); });
  }

  // ── WebSocket ─────────────────────────────────────────────────────────────

  // Called on every tick.  Creates a new socket when needed, respecting the
  // reconnect cooldown.  Idempotent when the socket is already CONNECTING (0)
  // or OPEN (1).
  function ensureWs() {
    if (!config || !config.wledWsUrl) return;
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
    if (Date.now() < wsRetryAt) return;

    // Discard any zombie socket before creating a new one.
    if (ws) { try { ws.close(); } catch (e) {} }

    try {
      ws = new WebSocket(config.wledWsUrl);
      ws.onopen  = function () { console.log('[wledtv] ws open'); };
      ws.onclose = function () {
        console.log('[wledtv] ws closed, retry in 2s');
        ws = null;
        wsRetryAt = Date.now() + 2000;
      };
      ws.onerror = function () { /* onclose fires next, handles cleanup */ };
    } catch (e) {
      ws = null;
      wsRetryAt = Date.now() + 2000;
    }
  }

  function closeWs() {
    wsRetryAt = 0;
    if (ws) { try { ws.close(); } catch (e) {} ws = null; }
  }

  // Attempt a fire-and-forget WebSocket send.  Returns false and resets ws on
  // error so ensureWs() will reconnect on the next tick.
  function trySend(payload) {
    if (!ws || ws.readyState !== 1) return false;
    if (ws.bufferedAmount > 16000) return false;
    try {
      ws.send(JSON.stringify(payload));
      return true;
    } catch (e) {
      ws = null;
      wsRetryAt = Date.now() + 2000;
      return false;
    }
  }

  function sendColors(colors) {
    var flat = [];
    colors.forEach(function (c) { flat.push(c[0], c[1], c[2]); });
    trySend({ on: true, bri: config.brightness, seg: [{ i: flat }] });
  }

  function sendOff() {
    if (!config) return;
    var total = (config.horizontalLedCount * 2 + config.verticalLedCount * 2) * 3;
    var zeros = [];
    for (var i = 0; i < total; i++) zeros.push(0);
    trySend({ on: false, seg: [{ i: zeros }] });
  }

  // ── Canvas / pixel sampling ───────────────────────────────────────────────

  function ensureCanvas() {
    if (!canvas) {
      canvas = document.createElement('canvas');
      ctx    = canvas.getContext('2d', { willReadFrequently: true });
    }
  }

  function sampleRegion(x, y, w, h) {
    x = Math.max(0, Math.min(Math.round(x), canvas.width  - 1));
    y = Math.max(0, Math.min(Math.round(y), canvas.height - 1));
    w = Math.max(1, Math.min(Math.round(w), canvas.width  - x));
    h = Math.max(1, Math.min(Math.round(h), canvas.height - y));
    var d = ctx.getImageData(x, y, w, h).data;
    var r = 0, g = 0, b = 0, n = d.length / 4;
    for (var i = 0; i < d.length; i += 4) { r += d[i]; g += d[i + 1]; b += d[i + 2]; }
    return [Math.round(r / n), Math.round(g / n), Math.round(b / n)];
  }

  // Colours are always built in clockwise order first:
  //   BottomCenter: right-half bottom → right → top → left → left-half bottom
  //   BottomLeft  : full bottom L→R → right B→T → top R→L → left T→B
  //   BottomRight : right B→T → top R→L → left T→B → full bottom L→R
  // For counter-clockwise strips the array is reversed (CCW = CW backwards).
  function computeLedColors() {
    var vw    = canvas.width,  vh = canvas.height;
    var h     = config.horizontalLedCount;
    var v     = config.verticalLedCount;
    var d     = config.sampleDepth;
    var dw    = Math.max(1, Math.round(vw * d));
    var dh    = Math.max(1, Math.round(vh * d));
    var start = config.loopStart; // 0=BottomCenter, 1=BottomLeft, 2=BottomRight

    var colors = [];

    function sampleH(i, n, edgeY, edgeH) {
      return sampleRegion((vw / n) * i, edgeY, vw / n, edgeH);
    }
    function sampleV(i, n, edgeX, edgeW) {
      return sampleRegion(edgeX, (vh / n) * i, edgeW, vh / n);
    }

    if (start === 1) {
      // BottomLeft
      for (var i = 0; i < h; i++) colors.push(sampleH(i, h, vh - dh, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));
    } else if (start === 2) {
      // BottomRight
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));
      for (var i = 0; i < h; i++) colors.push(sampleH(i, h, vh - dh, dh));
    } else {
      // BottomCenter (default)
      var hRight = Math.ceil(h / 2);
      var hLeft  = Math.floor(h / 2);
      for (var i = 0; i < hRight; i++) colors.push(sampleH(hLeft + i, h, vh - dh, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));
      for (var i = 0; i < hLeft; i++) colors.push(sampleH(i, h, vh - dh, dh));
    }

    return colors;
  }

  // ── Main tick ─────────────────────────────────────────────────────────────
  //
  // Design principles:
  //   • running + tickTimer are the only loop controls (no generation counter).
  //   • ensureWs() is always called, regardless of video readyState, so the
  //     connection stays warm during HLS buffering / startup.
  //   • drawImage is guarded by readyState >= 2 (HAVE_CURRENT_DATA) to avoid
  //     touching MSE video before it has decoded its first frame.

  function tick() {
    tickTimer = null;
    if (!running) return;

    // Stop if plugin was disabled mid-playback
    if (!config || !config.enabled) { stop(true); return; }

    // Stop (and turn off LEDs) when the video element is gone or ended
    var video = document.querySelector('video');
    if (!video || video.ended) { stop(true); return; }

    // Always maintain the WebSocket regardless of video state so we are ready
    // to send as soon as readyState reaches HAVE_CURRENT_DATA (2).
    ensureWs();

    var elapsed = 0;

    if (!video.paused && video.readyState >= 2) {
      var t0 = Date.now();
      try {
        ensureCanvas();
        canvas.width  = video.videoWidth;
        canvas.height = video.videoHeight;
        ctx.drawImage(video, 0, 0);
        var colors = computeLedColors();
        if (config.direction === 0) colors.reverse(); // 0 = Clockwise
        sendColors(colors);
        ledsOn = true;
      } catch (e) {
        // drawImage / getImageData throws on DRM content — skip frame, keep looping
      }
      elapsed = Date.now() - t0;
    } else {
      // Log why we are waiting (rate-limited to once every 3 s)
      var now = Date.now();
      if (now - _logAt > 3000) {
        _logAt = now;
        console.log('[wledtv] wait: paused=' + video.paused +
          ' readyState=' + video.readyState +
          ' ws=' + (ws ? ws.readyState : 'null') +
          ' wsRetry=' + Math.max(0, wsRetryAt - now) + 'ms');
      }
    }

    if (!running) return;
    var interval = config.updateIntervalMs || 100;
    tickTimer = setTimeout(tick, Math.max(0, interval - elapsed));
  }

  function start() {
    if (running) return;
    running = true;
    ledsOn  = false;
    console.log('[wledtv] start');
    tick(); // first tick immediately
  }

  // turnOff=true  → send LEDs-off and close WebSocket before stopping
  // turnOff=false → just stop the loop (e.g. when disabled via config)
  function stop(turnOff) {
    if (!running && !turnOff) return;
    running = false;
    if (tickTimer) { clearTimeout(tickTimer); tickTimer = null; }
    console.log('[wledtv] stop (off=' + !!turnOff + ')');
    if (turnOff && ledsOn) { sendOff(); ledsOn = false; }
    if (turnOff) closeWs();
  }

  // ── Poll ─────────────────────────────────────────────────────────────────
  // Fires every second.  Starts the loop when a playing video is detected.
  // Never stops the loop — that is handled exclusively inside tick().

  function poll() {
    if (running) return;
    var video = document.querySelector('video');
    if (video && !video.ended) {
      loadConfig(function () { if (config && config.enabled) start(); });
    }
  }

  setInterval(poll, 1000);
  poll();
})();
";
}