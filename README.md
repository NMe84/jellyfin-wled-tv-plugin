# <img src="https://raw.githubusercontent.com/NMe84/jellyfin-wled-tv-plugin/master/wledtv.png" height="32"> WLED TV — Edge Lighting for Jellyfin

A Jellyfin plugin that drives a [WLED](https://kno.wled.ge/) LED strip in real time based on the colours at the edges of whatever is playing. Sampling happens **on the server**: Jellyfin decodes the playing video with its bundled ffmpeg, samples the edges of each frame, and streams the colours to WLED over a WebSocket. Nothing runs in the browser and nothing is injected into the web client, so it works on any client — including smart-TV apps (LG WebOS, etc.) where the browser cannot read the video surface.

## Features

- **Server-side sampling** — the Jellyfin server decodes the video and talks to WLED directly. No browser involvement, no JavaScript injection, no client compatibility problems.
- **Hardware-accelerated & HDR-aware** — decoding uses the server's hardware acceleration; HDR content (HDR10 / HLG) is tone-mapped to SDR so the LED colours match what the TV shows.
- **Per-device activation** — the server samples only the client you select, so the extra decode is never incurred for other users on the same server.
- **Frame-synced** — the LED timing is slaved to the TV's reported playback clock (re-anchored continuously so it can't drift), and follows pause and seek. A tunable **sync delay** then lines the LEDs up with the picture, compensating for your TV's display lag.
- **Letterbox / pillarbox aware** — the LEDs are mapped to where the picture actually sits on the panel; LEDs over black bars stay dark instead of the picture being stretched across the whole strip. Works both for an aspect mismatch between the video and the screen (e.g. a 4:3 video on a 16:9 TV) and for bars baked into the file. Toggled independently for horizontal and vertical bars.
- **Configurable strip layout** — start position (bottom centre / left / right), direction (clockwise / counter-clockwise), LED counts per edge, and brightness.
- **Inline connection test** — the Test button opens a WebSocket from the server to the URL you typed, matching the runtime path, before you save.

## Requirements

- Jellyfin 10.11 or later
- Hardware video decoding enabled in Jellyfin (Dashboard → Playback) is recommended, especially for 4K content
- A WLED device reachable from the **Jellyfin server** over WebSocket (`ws://…/ws`)

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
| Active on device | The client whose playback drives the strip. The server samples only this device, so no extra decode happens for anyone else. "Any device" samples whichever session is playing (and decodes for every device that plays) |
| WLED WebSocket URL | Full address of your WLED device, e.g. `ws://192.168.1.50/ws`. The Jellyfin server connects to it directly |
| Horizontal / Vertical LEDs | Number of LEDs along each edge |
| Strip start position | Where LED #0 sits on the physical strip |
| Strip direction | Which way the strip runs from the start point |
| Brightness | Master brightness sent to WLED (0–255) |
| Sync delay (ms) | Shifts the LEDs to match the picture, compensating for the TV's display lag. Range −5000…5000. If the lighting runs *ahead* of the video, increase it (LEDs later); if it lags *behind*, decrease it into negative values (LEDs earlier — the server decodes further ahead). Depends on the TV's picture mode. 0 = no shift |
| Update LEDs in batches | Splits each colour frame into 54-LED batches. **Required for ESP8266** and other controllers with limited memory (disabling causes error 9 on those devices). Turn **off** on ESP32 and other controllers with ample heap to send the whole strip in one message per frame |
| Detect letterboxing | When on, the top/bottom LEDs map to the video's top/bottom edge and light up; when off they stay dark. Either way the LEDs over the bars stay off |
| Detect pillarboxing | When on, the left/right LEDs map to the video's side edges and light up; when off they stay dark. Either way the LEDs over the bars stay off |

## How it works

When the selected device starts playing a video, the server launches its bundled ffmpeg to decode the file from the current position (paced to real time, hardware-accelerated, tone-mapped if the source is HDR), scaled down to a small frame sized to your LED counts. Each decoded frame is sampled along the edges — excluding letterbox/pillarbox bars — and buffered with its own content timestamp. A clock model, re-anchored on every playback-progress event so it never drifts, releases each frame to WLED at the right moment, offset by the configured **sync delay** to account for the TV's display lag. Pause and seek are tracked through Jellyfin's session events so the lighting follows the picture, and the strip is turned off when playback stops.

## License

[MIT](LICENSE)
