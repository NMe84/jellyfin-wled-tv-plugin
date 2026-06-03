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
/// and posts the colours to our /WledTv/leds endpoint which forwards them to WLED.
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
    //   • X-Emby-Token header for Jellyfin auth (no quoting needed).
    private const string EdgeLightingScript = @"
(function () {
  'use strict';

  var config       = null;
  var canvas       = null;
  var ctx          = null;
  var lastCfgFetch = 0;
  var loopGen      = 0;
  var loopRunning  = false;
  var ledsOn       = false;

  // ── WebSocket to WLED (direct, no server proxy) ───────────────────────────────
  var ws              = null;
  var wsReconnecting  = false;

  // ── Config loading ──────────────────────────────────────────────────────────

  function getBaseUrl() {
    return window.ApiClient ? window.ApiClient.serverAddress() : window.location.origin;
  }
  function getToken() {
    return window.ApiClient ? window.ApiClient.accessToken() : '';
  }

  function loadConfig(cb) {
    var now = Date.now();
    if (config && now - lastCfgFetch < 30000) { cb(); return; }
    fetch(getBaseUrl() + '/WledTv/config', {
      headers: { 'X-Emby-Token': getToken() }
    })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (c) {
      if (c) { config = c; lastCfgFetch = now; }
      cb();
    })
    .catch(function () { cb(); });
  }

  // ── Canvas helpers ──────────────────────────────────────────────────────────

  function ensureCanvas() {
    if (!canvas) {
      canvas = document.createElement('canvas');
      ctx    = canvas.getContext('2d', { willReadFrequently: true });
    }
  }

  function sampleRegion(x, y, w, h) {
    // Clamp to canvas bounds
    x = Math.max(0, Math.min(Math.round(x), canvas.width  - 1));
    y = Math.max(0, Math.min(Math.round(y), canvas.height - 1));
    w = Math.max(1, Math.min(Math.round(w), canvas.width  - x));
    h = Math.max(1, Math.min(Math.round(h), canvas.height - y));
    var d = ctx.getImageData(x, y, w, h).data;
    var r = 0, g = 0, b = 0, n = d.length / 4;
    for (var i = 0; i < d.length; i += 4) { r += d[i]; g += d[i+1]; b += d[i+2]; }
    return [Math.round(r / n), Math.round(g / n), Math.round(b / n)];
  }

  // ── LED colour calculation ──────────────────────────────────────────────────
  //
  // Colours are always computed in the clockwise order first:
  //   BottomCenter: right-half of bottom → right side → top → left side → left-half of bottom
  //   BottomLeft  : full bottom (L→R) → right side → top (R→L) → left side (T→B)
  //   BottomRight : right side (B→T) → top (R→L) → left side (T→B) → full bottom (L→R)
  //
  // For counter-clockwise strips the array is simply reversed: CCW is CW backwards.

  function computeLedColors() {
    var vw = canvas.width, vh = canvas.height;
    var h  = config.horizontalLedCount;
    var v  = config.verticalLedCount;
    var d  = config.sampleDepth; // fraction of frame to sample from each edge
    var dw = Math.max(1, Math.round(vw * d));
    var dh = Math.max(1, Math.round(vh * d));
    var start = config.loopStart; // 0=BottomCenter, 1=BottomLeft, 2=BottomRight

    var colors = [];

    // Helper: sample the i-th band of n equal bands along a horizontal edge (y fixed)
    function sampleH(i, n, edgeY, edgeH) {
      var bandW = vw / n;
      return sampleRegion(i * bandW, edgeY, bandW, edgeH);
    }
    // Helper: sample the i-th band of n equal bands along a vertical edge (x fixed)
    function sampleV(i, n, edgeX, edgeW) {
      var bandH = vh / n;
      return sampleRegion(edgeX, i * bandH, edgeW, bandH);
    }

    if (start === 1) {
      // BottomLeft: bottom L→R, right B→T, top R→L, left T→B
      for (var i = 0; i < h; i++) colors.push(sampleH(i, h, vh - dh, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));

    } else if (start === 2) {
      // BottomRight: right B→T, top R→L, left T→B, bottom L→R
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));
      for (var i = 0; i < h; i++) colors.push(sampleH(i, h, vh - dh, dh));

    } else {
      // BottomCenter (default): right-half-bottom→right→top→left→left-half-bottom
      var hRight = Math.ceil(h / 2);
      var hLeft  = Math.floor(h / 2);

      // Bottom right half: from center (index hLeft) to right (index h-1)
      for (var i = 0; i < hRight; i++) colors.push(sampleH(hLeft + i, h, vh - dh, dh));
      // Right side: bottom to top
      for (var i = 0; i < v; i++) colors.push(sampleV(v - 1 - i, v, vw - dw, dw));
      // Top: right to left
      for (var i = h - 1; i >= 0; i--) colors.push(sampleH(i, h, 0, dh));
      // Left side: top to bottom
      for (var i = 0; i < v; i++) colors.push(sampleV(i, v, 0, dw));
      // Bottom left half: from left (index 0) to center (index hLeft-1)
      for (var i = 0; i < hLeft; i++) colors.push(sampleH(i, h, vh - dh, dh));
    }

    return colors;
  }

  // ── WebSocket management ────────────────────────────────────────────────────

  function openWebSocket() {
    if (wsReconnecting) return;
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return; // CONNECTING or OPEN
    if (!config || !config.wledWsUrl) return;

    try {
      var socket = new WebSocket(config.wledWsUrl);
      ws = socket;
      socket.onopen  = function () { console.log('[wledtv] ws: opened'); };
      socket.onclose = function () {
        var wasCurrent = ws === socket;
        if (wasCurrent) {
          ws = null;
          wsReconnecting = true;
          setTimeout(function () { wsReconnecting = false; }, 2000);
        }
        console.log('[wledtv] ws: closed (current=' + wasCurrent + ', reconn=' + wsReconnecting + ')');
      };
      socket.onerror = function () { /* onclose fires after onerror */ };
    } catch (e) {
      ws = null;
    }
  }

  function closeWebSocket() {
    wsReconnecting = false;
    if (ws) {
      try { ws.close(); } catch (e) {}
      ws = null;
    }
  }

  // ── Colour output (direct WebSocket to WLED) ──────────────────────────────

  var _lastDropLog = 0;
  var _sentCount   = 0;
  var _dropCount   = 0;

  function sendColors(colors) {
    openWebSocket();

    var now = Date.now();
    var logInterval = 3000; // only log drop reason once every 3 s

    if (!ws || ws.readyState !== 1) {
      _dropCount++;
      if (now - _lastDropLog > logInterval) {
        _lastDropLog = now;
        console.log('[wledtv] frame drop: ws not ready (state=' +
          (ws ? ws.readyState : 'null') + '), sent=' + _sentCount + ' dropped=' + _dropCount);
      }
      return;
    }

    if (ws.bufferedAmount > 16000) {
      _dropCount++;
      if (now - _lastDropLog > logInterval) {
        _lastDropLog = now;
        console.log('[wledtv] frame drop: buffer full (' + ws.bufferedAmount +
          ' bytes), sent=' + _sentCount + ' dropped=' + _dropCount);
      }
      return;
    }

    var flat = [];
    colors.forEach(function (c) { flat.push(c[0], c[1], c[2]); });
    try {
      ws.send(JSON.stringify({
        on:  true,
        bri: config.brightness,
        seg: [{ i: flat }]
      }));
      _sentCount++;
    } catch (e) {
      console.log('[wledtv] ws.send threw: ' + e);
      ws = null; // force reconnect next frame
    }
  }

  function sendOff() {
    if (!ws || ws.readyState !== 1) return;
    var total = config.horizontalLedCount * 2 + config.verticalLedCount * 2;
    var zeros = [];
    for (var i = 0; i < total * 3; i++) zeros.push(0);
    try {
      ws.send(JSON.stringify({ on: false, seg: [{ i: zeros }] }));
    } catch (e) {}
  }

  // ── Sampling loop ────────────────────────────────────────────────────────────
  //
  // WebSocket.send() is synchronous (buffers locally), so there is no
  // request/response cycle to wait for. The loop just paces itself to
  // updateIntervalMs wall-clock time, accounting for how long canvas
  // sampling took.

  function sampleStep(gen) {
    if (gen !== loopGen || !loopRunning) return;
    if (!config || !config.enabled) {
      console.log('[wledtv] stopping: config disabled');
      stopSampling(); return;
    }

    var video = document.querySelector('video');

    // Video gone entirely → turn LEDs off and stop
    if (!video || video.ended) {
      console.log('[wledtv] stopping: video ' + (video ? 'ended' : 'gone'));
      turnOffLeds(); return;
    }

    var frameStart = Date.now();

    // Only sample when actually playing with valid dimensions.
    // If paused or buffering (videoWidth === 0) we skip the frame but keep
    // the loop alive — stopping here caused a permanent stall because
    // checkState's restart condition also requires videoWidth > 0, so the
    // loop could never restart while the video was in a transient state.
    if (!video.paused && video.videoWidth > 0) {
      try {
        ensureCanvas();
        canvas.width  = video.videoWidth;
        canvas.height = video.videoHeight;
        ctx.drawImage(video, 0, 0);
        var colors = computeLedColors();
        if (config.direction === 0) colors.reverse(); // 0 = Clockwise
        sendColors(colors);
      } catch (err) {
        // getImageData throws on DRM content — skip frame, keep looping
      }
    }

    if (gen !== loopGen || !loopRunning) return;
    var elapsed = Date.now() - frameStart;
    var delay   = Math.max(0, (config.updateIntervalMs || 100) - elapsed);
    setTimeout(function () { sampleStep(gen); }, delay);
  }

  function startSampling() {
    if (loopRunning) return;
    loopRunning = true;
    ledsOn = true;
    loopGen++;
    openWebSocket();
    console.log('[wledtv] loop started (gen=' + loopGen + ')');
    sampleStep(loopGen);
  }

  function stopSampling() {
    loopRunning = false;
    loopGen++;
  }

  function turnOffLeds() {
    var wasOn = ledsOn;
    stopSampling();
    if (!wasOn) return;
    ledsOn = false;
    sendOff();
    closeWebSocket();
  }

  // ── Page / playback detection ───────────────────────────────────────────────
  // checkState is ONLY responsible for starting the loop when a video appears.
  // Stopping is handled exclusively by sampleStep — avoids false stops when
  // document.querySelector('video') transiently returns null mid-playback.

  function checkState() {
    var video = document.querySelector('video');
    if (video && !video.ended && !loopRunning) {
      loadConfig(function () { if (config && config.enabled) startSampling(); });
    }
  }

  setInterval(checkState, 1000);
  checkState();
})();
";
}