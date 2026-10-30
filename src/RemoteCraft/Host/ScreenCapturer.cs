using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteCraft.Host;

/// <summary>
/// Captures one monitor with GDI and reports which 64-px tiles changed since the previous capture.
/// Changed tiles in a row are merged into strips so each strip becomes one JPEG.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    const int Tile = 64;
    Bitmap _bmp;
    Graphics _g;
    byte[] _prev, _cur;
    int _stride;

    public Bitmap Image => _bmp;
    public Rectangle Bounds { get; private set; }

    public List<Rectangle> Capture(Rectangle bounds, bool full)
    {
        if (_bmp == null || bounds.Size != Bounds.Size)
        {
            _g?.Dispose();
            _bmp?.Dispose();
            _bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            _g = Graphics.FromImage(_bmp);
            _prev = null;
            full = true;
        }
        Bounds = bounds;

        _g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        DrawCursor(bounds);

        var data = _bmp.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            _stride = data.Stride;
            int size = _stride * bounds.Height;
            if (_cur == null || _cur.Length != size) _cur = new byte[size];
            Marshal.Copy(data.Scan0, _cur, 0, size);
        }
        finally { _bmp.UnlockBits(data); }

        var rects = new List<Rectangle>();
        int w = bounds.Width, h = bounds.Height;
        if (full || _prev == null || _prev.Length != _cur.Length)
        {
            for (int y = 0; y < h; y += Tile)
                rects.Add(new Rectangle(0, y, w, Math.Min(Tile, h - y)));
        }
        else
        {
            for (int ty = 0; ty < h; ty += Tile)
            {
                int th = Math.Min(Tile, h - ty);
                int runStart = -1;
                for (int tx = 0; tx < w; tx += Tile)
                {
                    int tw = Math.Min(Tile, w - tx);
                    bool changed = TileChanged(tx, ty, tw, th);
                    if (changed && runStart < 0) runStart = tx;
                    if (!changed && runStart >= 0)
                    {
                        rects.Add(new Rectangle(runStart, ty, tx - runStart, th));
                        runStart = -1;
                    }
                }
                if (runStart >= 0) rects.Add(new Rectangle(runStart, ty, w - runStart, th));
            }
        }
        (_prev, _cur) = (_cur, _prev);
        return rects;
    }

    bool TileChanged(int x, int y, int w, int h)
    {
        int bytes = w * 4;
        for (int row = y; row < y + h; row++)
        {
            int off = row * _stride + x * 4;
            if (!_cur.AsSpan(off, bytes).SequenceEqual(_prev.AsSpan(off, bytes))) return true;
        }
        return false;
    }

    void DrawCursor(Rectangle bounds)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!bounds.Contains(ci.pt)) return;
        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out var info))
        {
            hx = info.xHotspot;
            hy = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
        }
        var hdc = _g.GetHdc();
        try { Native.DrawIconEx(hdc, ci.pt.X - bounds.X - hx, ci.pt.Y - bounds.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
        finally { _g.ReleaseHdc(hdc); }
    }

    public void Dispose()
    {
        _g?.Dispose();
        _bmp?.Dispose();
    }
}

public sealed record MonitorBounds(string Name, Rectangle Bounds, bool Primary);

/// <summary>Monitors in physical pixels, independent of this process's (system) DPI awareness.</summary>
public static class Monitors
{
    public static List<MonitorBounds> GetAll()
    {
        var old = Native.SetThreadDpiAwarenessContext(Native.DPI_PER_MONITOR_AWARE_V2);
        try
        {
            var list = new List<MonitorBounds>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
                if (Native.GetMonitorInfo(h, ref mi))
                {
                    var r = mi.rcMonitor;
                    list.Add(new MonitorBounds(mi.szDevice.Replace(@"\\.\", ""), Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), (mi.dwFlags & 1) != 0));
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
        finally { Native.SetThreadDpiAwarenessContext(old); }
    }
}

public static class Jpeg
{
    static readonly ImageCodecInfo Codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] Encode(Bitmap source, Rectangle rect, long quality)
    {
        using var part = source.Clone(rect, PixelFormat.Format24bppRgb);
        using var ms = new MemoryStream();
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        part.Save(ms, Codec, ep);
        return ms.ToArray();
    }
}
