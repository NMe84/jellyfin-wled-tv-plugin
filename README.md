# WLED TV — Edge Lighting for Jellyfin

A Jellyfin plugin that drives a [WLED](https://kno.wled.ge/) LED strip in real time based on the colours at the edges of whatever is playing. The browser samples pixel colours directly from the video element and sends them to WLED via WebSocket — no server proxy, no extra software.

## Features

- **Direct WebSocket connection** — the browser talks to WLED directly; Jellyfin's server is not in the data path
- **Automatic letterbox / pillarbox detection** — black bars are ignored so LEDs react to the actual picture, not the bars. Resampled every 2 seconds, so dynamic aspect-ratio changes (e.g. IMAX sequences) are handled automatically
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
   URL: `https://nme84.github.io/jellyfin-wled-tv-plugin/manifest.json`
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

## Testing without hardware

If you want to try the plugin before buying a WLED controller and LED strip, check out the companion mock project:

**[wled-ambilight-mock](https://github.com/NMe84/wled-ambilight-mock)** — a local WebSocket server that implements the WLED API and renders a live visual of the LED colours around a simulated TV frame. Point the plugin at `ws://localhost:8001`, start the mock, and you can see exactly how the edge lighting will look without any physical hardware.

## License

[MIT](LICENSE)