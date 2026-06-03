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
  // Sequential-loop state: each new loop gets a unique generation number.
  // Any pending setTimeout from a previous generation sees a mismatch and exits,
  // so we never have two concurrent loops and requests never pile up.
  var loopGen     = 0;
  var loopRunning = false;

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

  // ── Send to plugin (which proxies to WLED) ──────────────────────────────────

  function sendColors(colors) {
    var flat = [];
    colors.forEach(function (c) { flat.push(c[0], c[1], c[2]); });
    // Return the promise so the caller can wait for completion (backpressure).
    return fetch(getBaseUrl() + '/WledTv/leds', {
      method: 'POST',
      headers: {
        'X-Emby-Token': getToken(),
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ brightness: config.brightness, leds: flat })
    });
  }

  // ── Sequential sampling loop ─────────────────────────────────────────────────
  //
  // Each iteration waits for the WLED POST to complete before scheduling the
  // next one, so requests never pile up regardless of network latency.
  // A generation counter (loopGen) lets us safely cancel a loop: any pending
  // setTimeout that sees a stale generation simply exits without rescheduling.

  function sampleStep(gen) {
    if (gen !== loopGen || !loopRunning) return; // stale or stopped
    if (!config || !config.enabled) { loopRunning = false; return; }

    var video = document.querySelector('video');

    // Video element removed → playback stopped entirely, turn off LEDs
    if (!video || video.ended) { turnOffLeds(); return; }

    // Paused → stop the loop but keep the last colours on the strip
    if (video.paused || video.videoWidth === 0) { loopRunning = false; return; }

    var frameStart = Date.now(); // used for frame-pacing
    var stepDone   = false;      // ensures only one of (fetch, watchdog) schedules next

    // Watchdog: if the fetch is dropped and never resolves, restart after 5 s
    // rather than freezing the loop forever.
    var watchdog = setTimeout(function () {
      if (!stepDone && gen === loopGen && loopRunning) {
        stepDone = true;
        scheduleNext();
      }
    }, 5000);

    function scheduleNext() {
      clearTimeout(watchdog);
      if (gen !== loopGen || !loopRunning) return;
      // Frame pacing: subtract time already spent this iteration so the
      // wall-clock interval stays close to updateIntervalMs.
      var elapsed = Date.now() - frameStart;
      var delay   = Math.max(0, (config.updateIntervalMs || 100) - elapsed);
      setTimeout(function () { sampleStep(gen); }, delay);
    }

    function done() {
      if (stepDone) return; // watchdog already fired
      stepDone = true;
      scheduleNext();
    }

    try {
      ensureCanvas();
      canvas.width  = video.videoWidth;
      canvas.height = video.videoHeight;
      ctx.drawImage(video, 0, 0);
      var colors = computeLedColors();
      if (config.direction === 1) colors.reverse(); // 1 = CounterClockwise
      // Wait for the POST to complete before scheduling the next frame.
      sendColors(colors).then(done, done);
    } catch (err) {
      // getImageData may throw on DRM-protected content — just reschedule
      done();
    }
  }

  function startSampling() {
    if (loopRunning) return;
    loopRunning = true;
    loopGen++;
    sampleStep(loopGen);
  }

  function stopSampling() {
    loopRunning = false;
    loopGen++;     // invalidates any pending setTimeout callbacks
  }

  function turnOffLeds() {
    stopSampling();
    fetch(getBaseUrl() + '/WledTv/off', {
      method: 'POST',
      headers: { 'X-Emby-Token': getToken() }
    }).catch(function () {});
  }

  // ── Page / playback detection ───────────────────────────────────────────────

  function checkState() {
    var video = document.querySelector('video');
    if (video && !video.paused && !video.ended && video.videoWidth > 0) {
      // Playing — start loop if not already running
      if (!loopRunning) {
        loadConfig(function () { if (config && config.enabled) startSampling(); });
      }
    } else {
      if (loopRunning) {
        if (!video || video.ended) {
          // Video gone entirely → turn LEDs off
          turnOffLeds();
        } else {
          // Just paused → stop sampling but keep last colours
          stopSampling();
        }
      }
    }
  }

  setInterval(checkState, 1000);
  checkState();
})();
";
}