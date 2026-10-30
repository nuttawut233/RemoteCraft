using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using RemoteCraft.Client;
using RemoteCraft.Host;

namespace RemoteCraft.UI;

/// <summary>Shows the remote framebuffer and forwards mouse and keyboard input to it.</summary>
public sealed class ViewerControl : Control
{
    readonly ClientSession _s;
    readonly HashSet<(ushort vk, ushort scan, bool ext)> _pressed = new();
    Rectangle _dest;
    Point _mouse;
    Point _lastSent = new(-1, -1);
    KeyboardHook _hook;
    static Cursor _dotCursor;

    public bool Fit { get; set; } = true;
    /// <summary>Scale used when not fitting: 1 = original size.</summary>
    public float Zoom { get; set; } = 1f;
    public bool ViewOnly { get; set; }

    static readonly float[] ZoomSteps = { 0.25f, 0.33f, 0.5f, 0.67f, 0.75f, 0.9f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f };

    /// <summary>The scale currently on screen, whether fitting or zoomed.</summary>
    public float CurrentScale
    {
        get
        {
            var fs = FrameSize;
            return fs.IsEmpty || _dest.Width == 0 ? (Fit ? 1f : Zoom) : (float)_dest.Width / fs.Width;
        }
    }

    /// <summary>Moves one zoom step in or out from the current scale and leaves fit mode.</summary>
    public void StepZoom(bool zoomIn)
    {
        float cur = CurrentScale;
        float next = zoomIn
            ? ZoomSteps.FirstOrDefault(z => z > cur + 0.01f, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < cur - 0.01f, ZoomSteps[0]);
        Fit = false;
        Zoom = next;
        ComputeDest(FrameSize);
        Invalidate();
    }

    public ViewerControl(ClientSession session)
    {
        _s = session;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(0x3C, 0x3C, 0x3C);
        TabStop = true;
        Cursor = DotCursor;
        _s.FrameUpdated += OnFrameUpdated;
    }

    static Cursor DotCursor
    {
        get
        {
            if (_dotCursor != null) return _dotCursor;
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(Brushes.White, 5, 5, 6, 6);
                g.DrawEllipse(Pens.Black, 5, 5, 6, 6);
            }
            return _dotCursor = new Cursor(bmp.GetHicon());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.FrameUpdated -= OnFrameUpdated;
            _hook?.Dispose();
        }
        base.Dispose(disposing);
    }

    Size FrameSize
    {
        get
        {
            lock (_s.FrameLock) return _s.Frame?.Size ?? Size.Empty;
        }
    }

    void ComputeDest(Size fs)
    {
        if (fs.IsEmpty) { _dest = Rectangle.Empty; return; }
        if (Fit)
        {
            float scale = Math.Min((float)Width / fs.Width, (float)Height / fs.Height);
            int w = (int)(fs.Width * scale), h = (int)(fs.Height * scale);
            _dest = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }
        else
        {
            // Zoomed (1:1 or other). When the remote screen is bigger than the view, it pans with the mouse.
            int w = Math.Max(1, (int)(fs.Width * Zoom)), h = Math.Max(1, (int)(fs.Height * Zoom));
            int x = w <= Width ? (Width - w) / 2 : -(int)((long)(w - Width) * Math.Clamp(_mouse.X, 0, Width) / Math.Max(1, Width));
            int y = h <= Height ? (Height - h) / 2 : -(int)((long)(h - Height) * Math.Clamp(_mouse.Y, 0, Height) / Math.Max(1, Height));
            _dest = new Rectangle(x, y, w, h);
        }
    }

    void OnFrameUpdated(Rectangle[] rects)
    {
        var fs = FrameSize;
        var before = _dest;
        ComputeDest(fs);
        if (_dest != before || rects.Length > 24) { Invalidate(); return; }
        float sx = (float)_dest.Width / fs.Width, sy = (float)_dest.Height / fs.Height;
        foreach (var r in rects)
            Invalidate(new Rectangle(_dest.X + (int)(r.X * sx) - 2, _dest.Y + (int)(r.Y * sy) - 2, (int)(r.Width * sx) + 5, (int)(r.Height * sy) + 5));
    }

    protected override void OnResize(EventArgs e)
    {
        ComputeDest(FrameSize);
        base.OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        lock (_s.FrameLock)
        {
            var frame = _s.Frame;
            if (frame == null)
            {
                g.Clear(BackColor);
                return;
            }
            ComputeDest(frame.Size);
            using (var bg = new SolidBrush(BackColor))
            using (var region = new Region(e.ClipRectangle))
            {
                region.Exclude(_dest);
                g.FillRegion(bg, region);
            }
            var clip = Rectangle.Intersect(e.ClipRectangle, _dest);
            if (clip.IsEmpty) return;
            float sx = (float)frame.Width / _dest.Width, sy = (float)frame.Height / _dest.Height;
            var src = new RectangleF((clip.X - _dest.X) * sx, (clip.Y - _dest.Y) * sy, clip.Width * sx, clip.Height * sy);
            bool scaled = _dest.Width != frame.Width;
            g.InterpolationMode = scaled ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(frame, clip, src, GraphicsUnit.Pixel);
        }
    }

    // ---------- mouse ----------

    bool ToFrame(Point p, out int x, out int y)
    {
        var fs = FrameSize;
        x = y = 0;
        if (fs.IsEmpty || _dest.Width == 0) return false;
        x = (int)((p.X - _dest.X) * (long)fs.Width / _dest.Width);
        y = (int)((p.Y - _dest.Y) * (long)fs.Height / _dest.Height);
        x = Math.Clamp(x, 0, fs.Width - 1);
        y = Math.Clamp(y, 0, fs.Height - 1);
        return true;
    }

    static MouseBtn Map(MouseButtons b) => b switch
    {
        MouseButtons.Left => MouseBtn.Left,
        MouseButtons.Right => MouseBtn.Right,
        MouseButtons.Middle => MouseBtn.Middle,
        MouseButtons.XButton1 => MouseBtn.X1,
        MouseButtons.XButton2 => MouseBtn.X2,
        _ => MouseBtn.None,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (!Fit)
        {
            var before = _dest;
            ComputeDest(FrameSize);
            if (before != _dest) Invalidate();
        }
        if (ViewOnly || !ToFrame(e.Location, out var x, out var y)) return;
        if (x == _lastSent.X && y == _lastSent.Y) return;
        _lastSent = new Point(x, y);
        _s.SendMouse(MouseKind.Move, MouseBtn.None, x, y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (ViewOnly || !ToFrame(e.Location, out var x, out var y)) return;
        _s.SendMouse(MouseKind.Down, Map(e.Button), x, y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ViewOnly || !ToFrame(e.Location, out var x, out var y)) return;
        _s.SendMouse(MouseKind.Up, Map(e.Button), x, y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ViewOnly || !ToFrame(e.Location, out var x, out var y)) return;
        _s.SendMouse(MouseKind.Wheel, MouseBtn.None, x, y, e.Delta);
    }

    // ---------- keyboard ----------

    protected override bool IsInputKey(Keys keyData) => true;
    protected override bool IsInputChar(char charCode) => true;

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _hook ??= new KeyboardHook(SendHookedKey);
        _hook.Enabled = true;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        if (_hook != null) _hook.Enabled = false;
        ReleaseAll();
    }

    public void ReleaseAll()
    {
        foreach (var k in _pressed.ToArray()) _s.SendKey(k.vk, k.scan, k.ext, true);
        _pressed.Clear();
    }

    /// <summary>Keys Windows would otherwise act on locally (Win, Alt+Tab…) arrive here from the hook.</summary>
    bool SendHookedKey(int vk, int scan, bool ext, bool up)
    {
        if (ViewOnly || !ContainsFocus || Form.ActiveForm != FindForm()) return false;
        Track((ushort)vk, (ushort)scan, ext, up);
        return true;
    }

    void Track(ushort vk, ushort scan, bool ext, bool up)
    {
        var key = (vk, scan, ext);
        if (up) _pressed.Remove(key); else _pressed.Add(key);
        _s.SendKey(vk, scan, ext, up);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_KEYDOWN:
            case Native.WM_KEYUP:
            case Native.WM_SYSKEYDOWN:
            case Native.WM_SYSKEYUP:
                if (ViewOnly) break;
                long lp = m.LParam.ToInt64();
                ushort scan = (ushort)((lp >> 16) & 0xFF);
                bool ext = ((lp >> 24) & 1) != 0;
                bool up = m.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                Track((ushort)m.WParam.ToInt64(), scan, ext, up);
                return; // swallowed: the remote desk handles it
            case Native.WM_CHAR:
            case Native.WM_SYSCHAR:
                if (!ViewOnly) return;
                break;
            case 0x20E: // WM_MOUSEHWHEEL
                if (!ViewOnly)
                {
                    var pt = PointToClient(Cursor.Position);
                    if (ToFrame(pt, out var x, out var y))
                        _s.SendMouse(MouseKind.HWheel, MouseBtn.None, x, y, (short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                }
                return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>Low-level keyboard hook that captures system keys (Win, Alt+Tab, Alt+Esc, Ctrl+Esc) for the viewer.</summary>
sealed class KeyboardHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    readonly HookProc _proc;
    readonly Func<int, int, bool, bool, bool> _handler;
    IntPtr _hook;

    public KeyboardHook(Func<int, int, bool, bool, bool> handler)
    {
        _handler = handler;
        _proc = Proc;
    }

    public bool Enabled
    {
        set
        {
            if (value && _hook == IntPtr.Zero) _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0);
            else if (!value && _hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool up = (k.flags & 0x80) != 0;
            bool ext = (k.flags & 1) != 0;
            bool alt = (k.flags & 0x20) != 0;
            bool ctrl = GetAsyncKeyState(0x11) < 0;
            bool system = k.vkCode is 0x5B or 0x5C or 0x5D           // Win, Apps
                          || (alt && k.vkCode is 0x09 or 0x1B)        // Alt+Tab, Alt+Esc
                          || (ctrl && k.vkCode == 0x1B);              // Ctrl+Esc
            if (system && _handler(k.vkCode, k.scanCode, ext, up)) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Enabled = false;
}
