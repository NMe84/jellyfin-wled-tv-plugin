using System;

namespace Jellyfin.Plugin.WledTv.Services;

/// <summary>
/// Turns a decoded RGB video frame into the per-LED colour array.
///
/// This is a direct port of the former client-side computeLedColors():
///   • bars baked into the video file are found by a symmetric pixel scan,
///     clamped to 21:9 (letterbox) / 4:3 (pillarbox);
///   • bars from an aspect mismatch between the video and the TV panel are
///     derived geometrically (video aspect vs panel aspect), since the decoded
///     frame is the raw video and never contains those bars;
///   • each LED is mapped to its position on the PANEL, sampled from the content
///     where it overlaps and left dark where it sits over a bar.
///
/// The frame is RGB24 (3 bytes/pixel), width*height*3 bytes, top-to-bottom.
/// The returned array is RGB per LED in strip order, length (2*h + 2*v) * 3.
/// </summary>
internal static class EdgeSampler
{
    private const int BlackThreshold = 16;   // per-channel "is black" cutoff
    private const double Depth = 0.015;      // sample 1.5% in from each content edge
    private const double Eps = 0.002;        // aspect-ratio noise guard

    public static byte[] Compute(
        byte[] px, int w, int h,
        int hCount, int vCount,
        LedLoopStart loopStart, LedLoopDirection direction,
        bool detectLetterbox, bool detectPillarbox,
        double panelAspect)
    {
        var colors = new byte[(2 * hCount + 2 * vCount) * 3];
        if (px == null || w <= 0 || h <= 0)
            return colors;

        // ── Bars baked into the video file (pixel scan) ───────────────────────
        int cLeft = 0, cRight = w, cTop = 0, cBottom = h;
        if (DetectBakedBars(px, w, h, out int vBar, out int hBar))
        {
            cTop = vBar; cBottom = h - vBar;
            cLeft = hBar; cRight = w - hBar;
        }
        double vw = cRight - cLeft;
        double vh = cBottom - cTop;
        double dw = Math.Max(1, Math.Round(vw * Depth));
        double dh = Math.Max(1, Math.Round(vh * Depth));

        // ── Display placement (video-vs-panel aspect) ────────────────────────
        double screenAR = panelAspect > 0 ? panelAspect : (double)hCount / Math.Max(1, vCount);
        double videoAR  = h > 0 ? (double)w / h : screenAR;
        double dispL = 0, dispR = 1, dispT = 0, dispB = 1;
        if (videoAR > screenAR)          // wider than panel → letterbox (top/bottom bars)
        {
            double vf = screenAR / videoAR; dispT = (1 - vf) / 2; dispB = 1 - dispT;
        }
        else if (videoAR < screenAR)     // narrower than panel → pillarbox (side bars)
        {
            double hf = videoAR / screenAR; dispL = (1 - hf) / 2; dispR = 1 - dispL;
        }

        // Content extent as fractions of the panel (composition of baked + display).
        double clS = dispL + (cLeft   / (double)w) * (dispR - dispL);
        double crS = dispL + (cRight  / (double)w) * (dispR - dispL);
        double ctS = dispT + (cTop    / (double)h) * (dispB - dispT);
        double cbS = dispT + (cBottom / (double)h) * (dispB - dispT);
        double spanH = crS - clS;
        double spanV = cbS - ctS;

        // Local sampling helpers (closures over the frame + geometry).
        (byte, byte, byte) SampleH(int i, int n, bool isTop)
        {
            double sx = (i + 0.5) / n;
            if (spanH <= 0 || sx < clS || sx > crS) return (0, 0, 0);
            bool overBar = isTop ? (ctS > Eps) : (cbS < 1 - Eps);
            if (overBar && !detectLetterbox) return (0, 0, 0);
            double cx = cLeft + ((sx - clS) / spanH) * vw;
            double cellW = vw / (n * spanH);
            double y = isTop ? cTop : (cBottom - dh);
            return SampleRegion(px, w, h, cx - cellW / 2, y, cellW, dh);
        }
        (byte, byte, byte) SampleV(int i, int n, bool isLeft)
        {
            double sy = (i + 0.5) / n;
            if (spanV <= 0 || sy < ctS || sy > cbS) return (0, 0, 0);
            bool overBar = isLeft ? (clS > Eps) : (crS < 1 - Eps);
            if (overBar && !detectPillarbox) return (0, 0, 0);
            double cy = cTop + ((sy - ctS) / spanV) * vh;
            double cellH = vh / (n * spanV);
            double x = isLeft ? cLeft : (cRight - dw);
            return SampleRegion(px, w, h, x, cy - cellH / 2, dw, cellH);
        }

        // ── Build the strip in clockwise order (matches the old client) ──────
        int idx = 0;
        void Push((byte r, byte g, byte b) c)
        {
            colors[idx * 3]     = c.r;
            colors[idx * 3 + 1] = c.g;
            colors[idx * 3 + 2] = c.b;
            idx++;
        }

        int H = hCount, V = vCount;
        if (loopStart == LedLoopStart.BottomLeft)
        {
            for (int i = 0; i < H; i++) Push(SampleH(i, H, false));
            for (int i = 0; i < V; i++) Push(SampleV(V - 1 - i, V, false));
            for (int i = H - 1; i >= 0; i--) Push(SampleH(i, H, true));
            for (int i = 0; i < V; i++) Push(SampleV(i, V, true));
        }
        else if (loopStart == LedLoopStart.BottomRight)
        {
            for (int i = 0; i < V; i++) Push(SampleV(V - 1 - i, V, false));
            for (int i = H - 1; i >= 0; i--) Push(SampleH(i, H, true));
            for (int i = 0; i < V; i++) Push(SampleV(i, V, true));
            for (int i = 0; i < H; i++) Push(SampleH(i, H, false));
        }
        else // BottomCenter
        {
            int hRight = (H + 1) / 2;
            int hLeft  = H / 2;
            for (int i = 0; i < hRight; i++) Push(SampleH(hLeft + i, H, false));
            for (int i = 0; i < V; i++) Push(SampleV(V - 1 - i, V, false));
            for (int i = H - 1; i >= 0; i--) Push(SampleH(i, H, true));
            for (int i = 0; i < V; i++) Push(SampleV(i, V, true));
            for (int i = 0; i < hLeft; i++) Push(SampleH(i, H, false));
        }

        // Colours are built clockwise; reverse for clockwise strips to match the
        // original behaviour (direction 0 = Clockwise).
        if (direction == LedLoopDirection.Clockwise)
            ReverseLeds(colors);

        return colors;
    }

    // Averages RGB over a region of the RGB24 frame (with clamping to bounds).
    private static (byte, byte, byte) SampleRegion(byte[] px, int fw, int fh, double xd, double yd, double wd, double hd)
    {
        int x = Math.Clamp((int)Math.Round(xd), 0, fw - 1);
        int y = Math.Clamp((int)Math.Round(yd), 0, fh - 1);
        int rw = Math.Clamp((int)Math.Round(wd), 1, fw - x);
        int rh = Math.Clamp((int)Math.Round(hd), 1, fh - y);
        long r = 0, g = 0, b = 0;
        for (int row = y; row < y + rh; row++)
        {
            int baseIdx = (row * fw + x) * 3;
            for (int col = 0; col < rw; col++)
            {
                int i = baseIdx + col * 3;
                r += px[i];
                g += px[i + 1];
                b += px[i + 2];
            }
        }
        long n = (long)rw * rh;
        return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    // Symmetric baked-bar detection, clamped to 21:9 / 4:3.  Returns false when
    // there are no bars to crop.
    private static bool DetectBakedBars(byte[] px, int w, int h, out int vBar, out int hBar)
    {
        vBar = 0; hBar = 0;
        int top = h, bottom = -1, left = w, right = -1;
        long nonBlack = 0;
        long total = (long)w * h;
        for (int row = 0; row < h; row++)
        {
            int baseIdx = row * w * 3;
            for (int col = 0; col < w; col++)
            {
                int i = baseIdx + col * 3;
                if (px[i] > BlackThreshold || px[i + 1] > BlackThreshold || px[i + 2] > BlackThreshold)
                {
                    nonBlack++;
                    if (row < top) top = row;
                    if (row > bottom) bottom = row;
                    if (col < left) left = col;
                    if (col > right) right = col;
                }
            }
        }

        if (nonBlack < total * 0.065) return false; // mostly-black frame

        int topBar = top;
        int bottomBar = (h - 1) - bottom;
        int leftBar = left;
        int rightBar = (w - 1) - right;

        // Symmetry: a genuine bar appears on both opposing sides; use the smaller.
        int v = Math.Min(topBar, bottomBar);
        int hh = Math.Min(leftBar, rightBar);

        // Clamp so content stays no more extreme than 21:9 / 4:3.
        int maxV = (int)Math.Floor((h - w * 9.0 / 21.0) / 2.0);
        int maxH = (int)Math.Floor((w - h * 4.0 / 3.0) / 2.0);
        if (v > maxV) v = maxV;
        if (hh > maxH) hh = maxH;
        if (v < 0) v = 0;
        if (hh < 0) hh = 0;

        vBar = v; hBar = hh;
        return v != 0 || hh != 0;
    }

    private static void ReverseLeds(byte[] colors)
    {
        int leds = colors.Length / 3;
        for (int i = 0, j = leds - 1; i < j; i++, j--)
        {
            for (int k = 0; k < 3; k++)
            {
                (colors[i * 3 + k], colors[j * 3 + k]) = (colors[j * 3 + k], colors[i * 3 + k]);
            }
        }
    }
}
