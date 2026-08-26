# <img src="https://raw.githubusercontent.com/NMe84/jellyfin-wled-tv-plugin/master/wledtv.png" height="32"> WLED TV — Edge Lighting for Jellyfin

A Jellyfin plugin that drives a [WLED](https://kno.wled.ge/) LED strip in real time based on the colours at the edges of whatever is playing. The browser samples pixel colours directly from the video element and sends them to WLED via WebSocket — no server proxy, no extra software.

## Features

- **Direct WebSocket connection** — the browser talks to WLED directly; Jellyfin's server is not in the data path
- **Letterbox / pillarbox aware** — LEDs are mapped to where the picture actually sits on the panel, so the LEDs over black bars stay dark instead of the picture being stretched across the whole strip. This works whether the bars come from an aspect-ratio mismatch between the video and the screen (e.g. a 4:3 video on a 16:9 TV) or are baked into the video file. Two independent toggles decide whether the strips lying *along* a bar light up (mapped to the video's edge) or stay dark. Baked-in-bar detection is symmetric (a bar must appear on both opposing sides) and capped at 21:9 for letterboxing and 4:3 for pillarboxing, so genuinely dark content is not mistaken for a bar. Resampled every 2 seconds, so dynamic aspect-ratio changes (e.g. IMAX sequences) are handled automatically
- **Per-device activation** — restrict edge lighting to one specific Jellyfin client so other devices on the same server are unaffected
- **Configurable strip layout** — set the start position (bottom centre / left / right), direction (clockwise / counter-clockwise), LED counts per edge, sample depth, brightness, and update rate
- **Inline connection test** — the Test button in settings opens a real WebSocket from your browser to the URL you typed, before you save

## Requirements

- Jellyfin 10.11 or later
- [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) plugin (listed as a dependency in the manifest)
- A WLED device reachable from the browser over WebSocket (`ws://…/ws`)

## Installation

1. Add the plugin repository to Jellyfin:  
   **Dashboard → Plugins → Repositories → +**  
   URL: `https://raw.githubusercontent.com/NMe84/jellyfin-plugins/gh-pages/manifest.json`
2. Install **WLED TV** from the catalogue and restart Jellyfin.
3. Go to **Dashboard → WLED TV** and configure your strip.

## Configuration

| Setting | Description |
|---|---|
| Enable edge lighting | Master on/off switch |
| Active on device | Restrict to one Jellyfin client (leave empty for all devices) |
| WLED WebSocket URL | Full address of your WLED device, e.g. `ws://192.168.1.50/ws` |
| Horizontal / Vertical LEDs | Number of LEDs along each edge |
| Strip start position | Where LED #0 sits on the physical strip |
| Strip direction | Which way the strip runs from the start point |
| Sample depth | How far from the screen edge to sample (% of frame dimension) |
| Brightness | Master brightness sent to WLED (0–255) |
| Update interval | Milliseconds between colour updates (100 ms = 10 fps) |
| Capture method | **Canvas 2D** (default) works on most platforms. **WebGL** works on devices where the video decoder renders to a hardware overlay inaccessible to Canvas 2D, such as LG WebOS. **Media Stream** reads decoded frames directly from the media track (via `MediaStreamTrackProcessor`), for platforms like WebOS 26 where even WebGL cannot read the overlay surface — non-DRM content only |
| Update LEDs in batches | Splits each colour frame into 54-LED batches. **Required for ESP8266** and other controllers with limited memory (disabling causes error 9 on those devices). Turn **off** on ESP32 and other controllers with ample heap to send the whole strip in a single message per frame instead of several |
| Detect letterboxing | Controls the top and bottom LED strips when the picture is letterboxed (black bars top and bottom, e.g. a 2.4:1 film). **On:** they are mapped to the video's top/bottom edge and light up. **Off:** they stay dark. Either way the LEDs over the bars themselves stay off, and the side strips light up only alongside the content |
| Detect pillarboxing | Controls the left and right LED strips when the picture is pillarboxed (black bars left and right, e.g. a 4:3 video on a 16:9 TV). **On:** they are mapped to the video's left/right edge and light up. **Off:** they stay dark. Either way the LEDs over the bars themselves stay off, and the top/bottom strips light up only where content reaches the edge |
| Debug mode | Shows a small on-screen timing overlay (capture time, frame gap, WebSocket state) on the playing device to help diagnose frame-rate problems, and — when connected to [wled-ambilight-mock](https://github.com/NMe84/wled-ambilight-mock) — streams diagnostic frame and state messages back through the WebSocket. Leave off in normal use |

## Testing without hardware

If you want to try the plugin before buying a WLED controller and LED strip, check out the companion mock project:

**[wled-ambilight-mock](https://github.com/NMe84/wled-ambilight-mock)** — a local WebSocket server that implements the WLED API and renders a live visual of the LED colours around a simulated TV frame. Point the plugin at `ws://localhost:8001`, start the mock, and you can see exactly how the edge lighting will look without any physical hardware.

## License

[MIT](LICENSE)