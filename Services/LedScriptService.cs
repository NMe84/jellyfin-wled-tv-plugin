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
  // Resolved once at startup — device ID never changes during a session.
  var _myDeviceId  = window.ApiClient ? window.ApiClient.deviceId() : '';

  var config       = null;   // last fetched config object
  var cfgAt        = 0;      // timestamp of last successful fetch

  var canvas       = null;
  var ctx          = null;

  var running      = false;  // true while the sampling loop is active
  var tickTimer    = null;   // handle returned by setTimeout (fallback scheduler)
  var _ticker      = null;   // Web Worker timer: null=not built, false=unavailable, else Worker
  var ledsOn       = false;  // true once we have sent at least one colour frame
  var noVideoTicks = 0;      // consecutive ticks with no usable video element

  // WebSocket state — deliberately simple: one live socket, one retry timestamp.
  var ws           = null;
  var wsRetryAt    = 0;      // do not attempt (re)connect before this timestamp
  var wsClosing    = false;  // true when WE initiated the close (suppress retry delay)

  var _logAt       = 0;      // rate-limit helper for the 'wait' diagnostic log

  // Mock-server detection and remote logging
  var isMock         = false;  // true once connected to the wled-ambilight-mock server
  var _mockChecked   = false;  // true once the state response has been inspected
  var _mockFirstFrame = true;  // true until the first frame has been logged this connection
  var _mockLogAt     = 0;      // rate-limit: last timestamp a frame was logged to mock

  // Letterbox / pillarbox detection
  var contentBounds = null;  // { top, bottom, left, right } in capture-frame px; null = full frame
  var lastBoundsAt  = 0;

  // Captured frame pixel data — written once per frame, read by sampleRegion
  var _framePixels = null;  // Uint8ClampedArray from a single getImageData read
  var _frameWidth  = 0;
  var _frameHeight = 0;

  // WebGL video-capture (used when config.captureMethod === 1)
  var _wglCanvas = null;   // offscreen WebGL canvas
  var _wglCtx    = null;   // WebGL context
  var _wglReady  = false;  // true once WebGL setup succeeded

  var _frame     = 0;      // frame counter, used to rotate batch send order

  // Aspect-ratio enforcement (WebOS WebGL path only)
  var _arKey          = '';    // last applied 'WxH' box, so we only touch styles on change
  var _videoStyleSaved = null; // original inline style attribute, restored on stop

  // On-screen diagnostics (rendered only when config.debugMode is enabled)
  var _diag        = null;   // overlay element, created lazily
  var _lastTickAt  = 0;      // timestamp of the previous tick, for inter-frame gap

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
      ws.onopen    = function () {
        console.log('[wledtv] ws open');
        ledsOn = false;  // resend on/bri after every (re)connect
        // Ask for the current state once.  The response lets us detect whether
        // this is the mock server (info.ver contains the suffix -mock) so we can
        // send debug log messages back through the same WebSocket, and whether
        // it is real WLED so we can switch to the binary live-data protocol.
        try { ws.send(JSON.stringify({ v: true })); } catch (e) {}
      };
      ws.onmessage = function (evt) {
        // Only inspect messages until we know whether this is the mock server.
        if (_mockChecked) return;
        try {
          var msg = JSON.parse(evt.data);
          if (msg && msg.info && typeof msg.info.ver === 'string') {
            _mockChecked = true;
            if (msg.info.ver.indexOf('-mock') !== -1) {
              isMock          = true;
              _mockFirstFrame = true;
              _mockLogAt      = 0;
              logToMock('wledtv attached (ver=' + msg.info.ver + ')');
            }
          }
        } catch (e) {}
      };
      ws.onclose   = function (ev) {
        ws           = null;
        isMock       = false;
        _mockChecked = false;
        if (wsClosing) {
          // We initiated this close — reconnect immediately (no penalty delay).
          console.log('[wledtv] ws closed (deliberate)');
        } else {
          // Remote/network close — short back-off before reconnecting.
          wsRetryAt = Date.now() + 200;
          console.log('[wledtv] ws closed unexpectedly (code=' + ev.code + '), retry in 200ms');
        }
        wsClosing = false;
      };
      ws.onerror   = function (ev) {
        console.log('[wledtv] ws error: ' + (ev.message || ev.type || 'unknown'));
      };
    } catch (e) {
      ws = null;
      wsRetryAt = Date.now() + 2000;
    }
  }

  function closeWs() {
    wsClosing = true;   // tell onclose not to impose a retry delay
    wsRetryAt = 0;
    if (ws) { try { ws.close(); } catch (e) {} ws = null; }
  }

  // Renders an on-screen timing overlay (debug mode only) so frame-rate issues
  // can be diagnosed directly on the TV without a remote console.  Removed again
  // as soon as debug mode is off or playback stops.
  function showDiag(text) {
    if (!config || !config.debugMode) {
      if (_diag && _diag.parentNode) { _diag.parentNode.removeChild(_diag); _diag = null; }
      return;
    }
    if (!_diag) {
      _diag = document.createElement('div');
      _diag.setAttribute('style',
        'position:fixed;top:8px;left:8px;z-index:2147483647;pointer-events:none;' +
        'font:12px/1.4 monospace;color:#0f0;background:rgba(0,0,0,.6);' +
        'padding:4px 6px;white-space:pre;border-radius:3px;');
      document.body.appendChild(_diag);
    }
    _diag.textContent = text;
  }

  function hideDiag() {
    if (_diag && _diag.parentNode) { _diag.parentNode.removeChild(_diag); _diag = null; }
  }

  // Send a log message back to the mock server.
  // No-op on real WLED or when debug mode is disabled in settings.
  function logToMock(msg) {
    if (!isMock || !config || !config.debugMode) return;
    if (!ws || ws.readyState !== 1) return;
    try { ws.send(JSON.stringify({ log: msg })); } catch (e) {}
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

  function toHex(n) {
    var h = '0123456789abcdef';
    n = Math.max(0, Math.min(255, Math.round(n)));
    return h[n >> 4] + h[n & 15];
  }

  function colorToHex(c) { return toHex(c[0]) + toHex(c[1]) + toHex(c[2]); }

  function sendColors(colors) {
    if (!ws || ws.readyState !== 1) return;

    // Frame-level flow control.  Never queue more than ~one frame ahead: if the
    // previous frame's bytes have not drained to the controller yet, drop THIS
    // frame entirely instead of growing an unbounded backlog.  Without this, a
    // controller that falls slightly behind accumulates a multi-frame buffer
    // that never recovers — observed as playback dropping to ~1 fps after the
    // first video while the WebSocket buffer stayed saturated.  This self-
    // throttles to the controller's real throughput; updateIntervalMs then only
    // sets the *maximum* rate, which is why lowering it had no effect.
    var maxQueued = colors.length * 8 + 512; // ≈ one full frame of JSON + margin
    if (ws.bufferedAmount > maxQueued) return;

    // Send on/bri once per connection rather than every frame.
    if (!ledsOn) {
      trySend({ on: true, bri: config.brightness });
    }
    if (config.batchUpdates) {
      // Batch mode: required for ESP8266 — ArduinoJson buffer limit is ~57 hex-string
      // LEDs per message (58 gives error 9 on tested device).
      // Compute the largest equal batch size ≤ 54 that divides the strip evenly.
      // For 260 LEDs this gives 52 (5 × 52 = 260); for 270 it stays at 54 (5 × 54 = 270).
      var numBatches = Math.ceil(colors.length / 54);
      var BATCH = Math.ceil(colors.length / numBatches);

      // Build the per-batch payloads for this frame.
      var batches = [];
      for (var start = 0; start < colors.length; start += BATCH) {
        var batchStart = (start + BATCH > colors.length && colors.length >= BATCH)
          ? colors.length - BATCH
          : start;
        var chunk = colors.slice(batchStart, batchStart + BATCH).map(colorToHex);
        batches.push({ seg: [{ i: batchStart === 0 ? chunk : [batchStart].concat(chunk) }] });
      }

      // Rotate the send order every frame as defence-in-depth: should a single
      // burst ever be partially dropped, the loss rotates across segments rather
      // than always hitting the final one.  With the flow control above the whole
      // burst normally fits in a drained buffer, so all segments stay in sync.
      var n   = batches.length;
      var off = n > 0 ? (_frame % n) : 0;
      for (var k = 0; k < n; k++) {
        trySend(batches[(off + k) % n]);
      }
      _frame++;
    } else {
      // Single-frame mode: ESP32 and other controllers with ample heap.
      // Sends all LEDs in one message — 1 msg/frame instead of 5.
      trySend({ seg: [{ i: colors.map(colorToHex) }] });
    }
  }

  function sendOff() {
    if (!config) return;
    // Turning off reverts the segment to effect mode (WLED docs: individual
    // control is non-persistent across power cycles).  A plain on:false is
    // sufficient; there is no need to zero-fill i: first.
    trySend({ on: false });
  }

  // ── Canvas / pixel sampling ───────────────────────────────────────────────

  function ensureCanvas() {
    if (!canvas) {
      canvas = document.createElement('canvas');
      ctx    = canvas.getContext('2d', { willReadFrequently: true });
    }
  }

  // ── Letterbox / pillarbox detection ──────────────────────────────────────
  //
  // Scans _framePixels (already downscaled) for black bars.
  // Coordinates are in the capture frame's own pixel space.
  // Called every 2 s so dynamic aspect-ratio changes are picked up quickly.

  function detectContentBounds() {
    var w = _frameWidth, h = _frameHeight;
    if (!_framePixels || w <= 0 || h <= 0) return null;
    var T = 16; // per-channel threshold

    // Single pass: track bounding box of non-black pixels and count them.
    // Replaces four separate row/column sweeps and is more cache-friendly.
    var top = h, bottom = -1, left = w, right = -1;
    var nonBlack = 0;
    var total = w * h;
    for (var i = 0; i < total; i++) {
      var base = i * 4;
      if (_framePixels[base] > T || _framePixels[base+1] > T || _framePixels[base+2] > T) {
        nonBlack++;
        var py = (i / w) | 0;
        var px = i % w;
        if (py < top)    top    = py;
        if (py > bottom) bottom = py;
        if (px < left)   left   = px;
        if (px > right)  right  = px;
      }
    }

    // Mostly-black frame (credits, fade-to-black, etc.) — the tiny bright region
    // is isolated text, not actual content edges.  Sampling from the full frame
    // is better than zooming into a few lines of white text and blasting white LEDs.
    if (nonBlack < total * 0.065) return null;

    // No bars found — full frame
    if (top === 0 && bottom === h - 1 && left === 0 && right === w - 1) return null;

    return { top: top, bottom: bottom + 1, left: left, right: right + 1 };
  }

  // ── WebGL video-capture fallback ─────────────────────────────────────────
  //
  // On platforms like WebOS the video decoder renders into a hardware overlay
  // layer that Canvas 2D ctx.drawImage() cannot read (returns all-black).
  // WebGL texture uploads use a different GPU path and can access those frames.
  // After 3 consecutive all-black Canvas 2D frames we transparently switch.

  // Releases the WebGL context and its GPU resources so the next capture builds
  // a fresh one.  Called on playback start and when the app is backgrounded, so
  // a context the TV degraded during a suspend/resume cycle cannot persist for
  // the life of the page — that degraded context is the cause of capture
  // dropping to ~1 fps (correct pixels, but very slow) after leaving and
  // re-entering Jellyfin.  loseContext() frees the GPU side immediately and the
  // old canvas is dropped for GC.  Browsers cap live contexts (~16), so we MUST
  // release before recreating, or repeated rebuilds would leak contexts.
  function _destroyWebGL() {
    if (_wglCtx) {
      try {
        var ext = _wglCtx.getExtension('WEBGL_lose_context');
        if (ext) ext.loseContext();
      } catch (e) {}
    }
    _wglCanvas = null;
    _wglCtx    = null;
    _wglReady  = false;
  }

  function _setupWebGL() {
    // Reuse the live context; rebuild only if it is missing or outright lost.
    // The slow-after-resume case (context alive but degraded) is handled by
    // _destroyWebGL() on playback start; this guard additionally recovers from
    // a context the browser reports as fully lost.
    if (_wglCanvas !== null) {
      if (_wglCtx && !_wglCtx.isContextLost()) return _wglReady;
      _destroyWebGL();
    }
    _wglCanvas = document.createElement('canvas');
    try {
      _wglCtx = _wglCanvas.getContext('webgl') ||
                _wglCanvas.getContext('experimental-webgl');
    } catch (e) { _wglCtx = null; }
    if (!_wglCtx) return (_wglReady = false);

    var g = _wglCtx;
    // UNPACK_FLIP_Y_WEBGL stores video row-0 (top) at texture y=1.
    // The vertex shader then inverts t.y so readPixels (which returns rows
    // bottom-first) ends up delivering data in top-to-bottom order, matching
    // Canvas 2D convention, without any JS post-processing.
    g.pixelStorei(g.UNPACK_FLIP_Y_WEBGL, true);

    var vs = g.createShader(g.VERTEX_SHADER);
    g.shaderSource(vs,
      'attribute vec2 p;varying vec2 t;' +
      'void main(){t=vec2(p.x*.5+.5,1.-(p.y*.5+.5));gl_Position=vec4(p,0,1);}');
    g.compileShader(vs);

    var fs = g.createShader(g.FRAGMENT_SHADER);
    g.shaderSource(fs,
      'precision lowp float;uniform sampler2D s;varying vec2 t;' +
      'void main(){gl_FragColor=texture2D(s,t);}');
    g.compileShader(fs);

    var prog = g.createProgram();
    g.attachShader(prog, vs); g.attachShader(prog, fs);
    g.linkProgram(prog); g.useProgram(prog);

    var buf = g.createBuffer();
    g.bindBuffer(g.ARRAY_BUFFER, buf);
    g.bufferData(g.ARRAY_BUFFER,
      new Float32Array([-1,-1, 1,-1, -1,1, 1,1]), g.STATIC_DRAW);
    var aPos = g.getAttribLocation(prog, 'p');
    g.enableVertexAttribArray(aPos);
    g.vertexAttribPointer(aPos, 2, g.FLOAT, false, 0, 0);

    var tex = g.createTexture();
    g.bindTexture(g.TEXTURE_2D, tex);
    g.texParameteri(g.TEXTURE_2D, g.TEXTURE_WRAP_S, g.CLAMP_TO_EDGE);
    g.texParameteri(g.TEXTURE_2D, g.TEXTURE_WRAP_T, g.CLAMP_TO_EDGE);
    g.texParameteri(g.TEXTURE_2D, g.TEXTURE_MIN_FILTER, g.LINEAR);
    g.uniform1i(g.getUniformLocation(prog, 's'), 0);

    return (_wglReady = true);
  }

  function _captureViaWebGL(video, tw, th) {
    if (!_setupWebGL()) return false;
    if (_wglCanvas.width !== tw || _wglCanvas.height !== th) {
      _wglCanvas.width  = tw;
      _wglCanvas.height = th;
      _wglCtx.viewport(0, 0, tw, th);
    }
    try {
      var g = _wglCtx;
      g.texImage2D(g.TEXTURE_2D, 0, g.RGBA, g.RGBA, g.UNSIGNED_BYTE, video);
      g.drawArrays(g.TRIANGLE_STRIP, 0, 4);
      // Read pixels directly from the WebGL framebuffer — no cross-context
      // ctx.drawImage copy, no separate getImageData call.  On slow SoCs the
      // cross-context flush (WebGL → Canvas 2D) was the dominant cost even at
      // 1/4 scale; staying inside one GL context eliminates that bottleneck.
      var buf = new Uint8Array(tw * th * 4);
      g.readPixels(0, 0, tw, th, g.RGBA, g.UNSIGNED_BYTE, buf);
      _framePixels = buf;
      return true;
    } catch (e) { return false; }
  }

  // Captures the current video frame at 1/4 scale into _framePixels.
  // Canvas 2D path: draws downscaled then reads the whole canvas once.
  // WebGL path: renders to GL canvas and reads back via gl.readPixels —
  //   no 2D canvas involved, no cross-context pipeline flush.
  function captureFrame(video) {
    var tw = Math.max(1, video.videoWidth  >> 2); // 1/4 scale
    var th = Math.max(1, video.videoHeight >> 2);
    _frameWidth  = tw;
    _frameHeight = th;
    if (config && config.captureMethod === 1) {
      _captureViaWebGL(video, tw, th);
    } else {
      ensureCanvas();
      canvas.width  = tw;
      canvas.height = th;
      ctx.drawImage(video, 0, 0, tw, th);
      _framePixels = ctx.getImageData(0, 0, tw, th).data;
    }
    // On the WebGL/WebOS path, reading the frame de-overlays the video and the
    // hardware media plane then stretches it to fill the element box, ignoring
    // CSS object-fit (confirmed: setting object-fit had no effect).  Give the
    // element box itself the content's aspect ratio so it cannot stretch.
    if (config && config.captureMethod === 1) {
      enforceAspect(video);
    }
  }

  // Constrains the video element's BOX to the content's aspect ratio so the
  // WebOS media plane (which ignores object-fit for hardware-decoded video and
  // stretches to fill the element box) cannot distort it.
  //
  // Critically, this ONLY sets width/height/margin — never position, inset, or
  // z-index.  Changing the box size does not affect paint order, so Jellyfin's
  // subtitle overlay keeps rendering above the video exactly as it did before
  // the plugin.  Earlier versions changed position/inset, which lifted the
  // video above the subtitle layer and hid the subtitles.  Centring is left to
  // the player's existing layout: its container centres the video (flex or
  // absolute inset), and margin:auto handles the remaining axis without
  // disturbing stacking.
  function enforceAspect(video) {
    if (!video.videoWidth || !video.videoHeight) return;
    var boxW = window.innerWidth  || document.documentElement.clientWidth;
    var boxH = window.innerHeight || document.documentElement.clientHeight;
    if (!boxW || !boxH) return;
    var ar = video.videoWidth / video.videoHeight;
    var W, H;
    if (boxW / boxH > ar) { H = boxH; W = Math.round(boxH * ar); }
    else                  { W = boxW; H = Math.round(boxW / ar); }
    var key = W + 'x' + H;
    if (key === _arKey) return;             // unchanged since last application
    if (_videoStyleSaved === null) _videoStyleSaved = video.getAttribute('style') || '';
    _arKey = key;
    var s = video.style;
    s.setProperty('width',      W + 'px', 'important');
    s.setProperty('height',     H + 'px', 'important');
    s.setProperty('max-width',  W + 'px', 'important');
    s.setProperty('max-height', H + 'px', 'important');
    s.setProperty('margin',     'auto',   'important'); // centres without changing stacking
    s.setProperty('object-fit', 'contain', 'important'); // harmless where honoured
  }

  // Restores the video element's original inline style (undoes enforceAspect).
  function restoreVideoStyle() {
    if (_videoStyleSaved === null) return;
    var v = document.querySelector('video');
    if (v) v.setAttribute('style', _videoStyleSaved);
    _videoStyleSaved = null;
    _arKey = '';
  }

  // Averages pixel colour over the given region of _framePixels.
  // No getImageData call — indexes the pre-read array directly.
  function sampleRegion(x, y, w, h) {
    if (!_framePixels) return [0, 0, 0];
    x = Math.max(0, Math.min(Math.round(x), _frameWidth  - 1));
    y = Math.max(0, Math.min(Math.round(y), _frameHeight - 1));
    w = Math.max(1, Math.min(Math.round(w), _frameWidth  - x));
    h = Math.max(1, Math.min(Math.round(h), _frameHeight - y));
    var r = 0, g = 0, b = 0;
    for (var row = y; row < y + h; row++) {
      var base = (row * _frameWidth + x) * 4;
      for (var col = 0; col < w; col++) {
        var i = base + col * 4;
        r += _framePixels[i];
        g += _framePixels[i + 1];
        b += _framePixels[i + 2];
      }
    }
    var n = w * h;
    return [Math.round(r / n), Math.round(g / n), Math.round(b / n)];
  }

  // Colours are always built in clockwise order first:
  //   BottomCenter: right-half bottom → right → top → left → left-half bottom
  //   BottomLeft  : full bottom L→R → right B→T → top R→L → left T→B
  //   BottomRight : right B→T → top R→L → left T→B → full bottom L→R
  // For counter-clockwise strips the array is reversed (CCW = CW backwards).
  function computeLedColors() {
    // Honour detected content bounds so bars are excluded from sampling.
    var b  = contentBounds;
    var bx = b ? b.left              : 0;
    var by = b ? b.top               : 0;
    var vw = b ? (b.right  - b.left) : _frameWidth;
    var vh = b ? (b.bottom - b.top)  : _frameHeight;
    var h     = config.horizontalLedCount;
    var v     = config.verticalLedCount;
    var d     = config.sampleDepth;
    var dw    = Math.max(1, Math.round(vw * d));
    var dh    = Math.max(1, Math.round(vh * d));
    var start = config.loopStart; // 0=BottomCenter, 1=BottomLeft, 2=BottomRight

    var colors = [];

    function sampleH(i, n, edgeY, edgeH) {
      return sampleRegion(bx + (vw / n) * i, by + edgeY, vw / n, edgeH);
    }
    function sampleV(i, n, edgeX, edgeW) {
      return sampleRegion(bx + edgeX, by + (vh / n) * i, edgeW, vh / n);
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

  // Drives the tick loop from a Web Worker timer.  The browser clamps the main
  // thread's setTimeout to 1 Hz when it considers the page backgrounded — which
  // WebOS does (incorrectly) after the app is suspended and resumed, turning the
  // ambilight into a ~1 fps slideshow even though capture itself takes ~5 ms.
  // Worker timers are exempt from that throttling, so the worker posts back after
  // each requested delay to keep ticks running at the configured rate.  Falls
  // back to setTimeout where Workers are unavailable or blocked by CSP.
  function ensureTicker() {
    if (_ticker !== null) return;
    try {
      var src = 'onmessage=function(e){setTimeout(function(){postMessage(0);},e.data);};';
      var url = URL.createObjectURL(new Blob([src], { type: 'application/javascript' }));
      _ticker = new Worker(url);
      _ticker.onmessage = function () { tick(); };
    } catch (e) {
      _ticker = false; // unavailable — fall back to setTimeout
    }
  }

  // Schedules the next tick after delay ms via the worker, or setTimeout if the
  // worker could not be created.  The worker fires once per request (not a
  // recurring interval), and tick() bails immediately when !running, so a stray
  // post after stop() simply no-ops — nothing to cancel.
  function scheduleNext(delay) {
    ensureTicker();
    if (_ticker) { _ticker.postMessage(delay); }
    else { tickTimer = setTimeout(tick, delay); }
  }

  // ── Main tick ─────────────────────────────────────────────────────────────
  //
  // Design principles:
  //   • running + scheduleNext are the only loop controls (no generation counter).
  //   • ensureWs() is always called, regardless of video readyState, so the
  //     connection stays warm during HLS buffering / startup.
  //   • drawImage is guarded by readyState >= 2 (HAVE_CURRENT_DATA) to avoid
  //     touching MSE video before it has decoded its first frame.

  function tick() {
    tickTimer = null;
    if (!running) return;

    // Stop if plugin was disabled mid-playback, or if this is the wrong device
    if (!config || !config.enabled) { stop(true); return; }
    if (config.deviceId && config.deviceId !== '' && _myDeviceId !== config.deviceId) { stop(true); return; }

    // Find the active video element.  Jellyfin may render the player inside a
    // same-origin iframe (ViewManager), so check those too.
    var video = document.querySelector('video');
    if (!video) {
      var frames = document.querySelectorAll('iframe');
      for (var fi = 0; fi < frames.length && !video; fi++) {
        try { video = frames[fi].contentDocument && frames[fi].contentDocument.querySelector('video'); }
        catch (e) {}
      }
    }

    // Stop only after 3 consecutive ticks (~300 ms) with no usable video.
    // A single missing tick is normal during HLS segment boundaries, quality
    // switches, and Jellyfin view transitions — stopping immediately caused the
    // 2-second reconnect cycle seen in practice.
    if (!video || video.ended) {
      noVideoTicks++;
      if (noVideoTicks >= 3) {
        noVideoTicks = 0;
        console.log('[wledtv] no video for 3 ticks, stopping');
        stop(true);
        return;
      }
      // Keep WebSocket warm while waiting; check again next tick.
      ensureWs();
      if (!running) return;
      scheduleNext(config.updateIntervalMs || 100);
      return;
    }
    noVideoTicks = 0;

    // Always maintain the WebSocket regardless of video state so we are ready
    // to send as soon as readyState reaches HAVE_CURRENT_DATA (2).
    ensureWs();

    var elapsed = 0;

    // Require a decoded frame with non-zero dimensions.
    // The paused check is intentionally omitted: on WebOS (and some other
    // smart-TV platforms) video.paused may be stuck at true even during active
    // playback because the underlying hardware media component does not keep the
    // HTML5 property in sync.  Sampling a paused frame is harmless — the LEDs
    // simply hold the last colour until playback resumes.
    if (video.readyState >= 2 && video.videoWidth > 0 && video.videoHeight > 0) {
      var t0 = Date.now();
      // Inter-frame gap: time since the previous processed tick.  A large gap
      // while capMs is small means the scheduler (setTimeout) is being throttled
      // rather than the capture being slow.
      var gap = _lastTickAt ? (t0 - _lastTickAt) : 0;
      _lastTickAt = t0;
      var capMs = 0, sendMs = 0;
      try {
        // captureFrame sets canvas + _framePixels at 1/4 scale
        captureFrame(video);
        capMs = Date.now() - t0;
        // Log frame diagnostics to the mock server periodically.
        // Read centre pixel directly from the pre-read array — no extra getImageData.
        if (isMock) {
          var tMock = Date.now();
          if (_mockFirstFrame || tMock - _mockLogAt > 10000) {
            _mockFirstFrame = false;
            _mockLogAt      = tMock;
            if (_frameWidth > 0 && _frameHeight > 0 && _framePixels) {
              var ci = ((_frameHeight >> 1) * _frameWidth + (_frameWidth >> 1)) * 4;
              logToMock('frame ' + video.videoWidth + 'x' + video.videoHeight +
                ' capture=' + _frameWidth + 'x' + _frameHeight +
                ' method=' + (config && config.captureMethod === 1 ? 'webgl' : 'canvas2d') +
                ' readyState=' + video.readyState +
                ' center=[' + _framePixels[ci] + ',' + _framePixels[ci+1] + ',' + _framePixels[ci+2] + ']' +
                (_framePixels[ci] + _framePixels[ci+1] + _framePixels[ci+2] === 0 ? ' (black)' : ''));
            } else {
              logToMock('frame 0x0 — no frame data');
            }
          }
        }
        // Re-detect content bounds every 2 s.  This handles both static bars
        // (letterbox/pillarbox) and dynamic aspect-ratio changes mid-video.
        var tFrame = Date.now();
        if (tFrame - lastBoundsAt > 2000) {
          contentBounds = detectContentBounds();
          lastBoundsAt  = tFrame;
        }
        var colors = computeLedColors();
        if (config.direction === 0) colors.reverse(); // 0 = Clockwise
        var tSend = Date.now();
        sendColors(colors);
        sendMs = Date.now() - tSend;
        ledsOn = true;
      } catch (e) {
        // drawImage / getImageData throws on DRM content — skip frame, keep looping
      }
      elapsed = Date.now() - t0;
      // Update the on-screen diagnostics overlay (no-op unless debug mode is on).
      showDiag(
        'wledtv ' + (config.captureMethod === 1 ? 'webgl' : 'canvas2d') + '\n' +
        'cap  ' + capMs  + 'ms\n' +
        'send ' + sendMs + 'ms\n' +
        'gap  ' + gap    + 'ms\n' +
        'ws   ' + (ws ? ws.readyState : 'null') + ' buf ' + (ws ? ws.bufferedAmount : '-'));
    } else {
      // Log to mock server why sampling is being skipped (rate-limited to 3 s)
      if (isMock) {
        var tMockSkip = Date.now();
        if (tMockSkip - _mockLogAt > 3000) {
          _mockLogAt = tMockSkip;
          logToMock('skip — readyState=' + video.readyState +
            ' videoWidth=' + video.videoWidth +
            ' videoHeight=' + video.videoHeight +
            ' paused=' + video.paused);
        }
      }
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
    scheduleNext(Math.max(0, interval - elapsed));
  }

  function start() {
    if (running) return;
    running = true;
    ledsOn  = false;
    // Start every playback with a fresh GPU context.  A context inherited from
    // before a suspend/resume cycle can be alive but degraded (correct pixels at
    // ~1 fps); rebuilding it here restores full-rate capture.  No-op on the
    // Canvas 2D path.
    _destroyWebGL();
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
    restoreVideoStyle(); // undo any aspect-ratio box sizing we applied
    hideDiag();          // remove the diagnostics overlay
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
      loadConfig(function () {
        if (!config || !config.enabled) return;
        if (config.deviceId && config.deviceId !== '' && _myDeviceId !== config.deviceId) return;
        start();
      });
    }
  }

  // When the app is backgrounded (e.g. leaving Jellyfin on the TV) stop cleanly
  // and drop the GPU context, so a context degraded by the suspend cannot linger
  // for the life of the page.  poll() rebuilds everything fresh on resume.
  document.addEventListener('visibilitychange', function () {
    if (document.hidden) { stop(true); _destroyWebGL(); }
  });

  setInterval(poll, 1000);
  poll();
})();
";
}