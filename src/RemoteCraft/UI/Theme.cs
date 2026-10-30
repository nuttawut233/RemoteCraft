using System.Drawing.Drawing2D;

namespace RemoteCraft.UI;

public static class Theme
{
    public static readonly Color Red = Color.FromArgb(0xEF, 0x44, 0x3B);
    public static readonly Color Orange = Color.FromArgb(0xF4, 0x7B, 0x2A);
    public static readonly Color Heading = Color.FromArgb(0xEC, 0x5A, 0x2E);
    public static readonly Color Green = Color.FromArgb(0x2B, 0x9B, 0x3B);
    public static readonly Color GreenHover = Color.FromArgb(0x24, 0x87, 0x32);
    public static readonly Color Gray = Color.FromArgb(0xCF, 0xCF, 0xCF);
    public static readonly Color GrayHover = Color.FromArgb(0xBD, 0xBD, 0xBD);
    public static readonly Color Blue1 = Color.FromArgb(0x3F, 0x86, 0xF4);
    public static readonly Color Blue2 = Color.FromArgb(0x22, 0x56, 0xD5);
    public static readonly Color Bg = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public static readonly Color Chrome = Color.FromArgb(0xEC, 0xEC, 0xEC);
    public static readonly Color Line = Color.FromArgb(0xD9, 0xD9, 0xD9);
    public static readonly Color Text = Color.FromArgb(0x33, 0x33, 0x33);
    public static readonly Color SubText = Color.FromArgb(0x6B, 0x6B, 0x6B);
    public static readonly Color Link = Color.FromArgb(0x1E, 0x6F, 0xD9);
    public static readonly Color Hover = Color.FromArgb(0xE2, 0xE2, 0xE2);
    public static readonly Color Online = Color.FromArgb(0x35, 0xA8, 0x45);

    /// <summary>System DPI / 96. Fonts are in points and scale by themselves; layout pixels go through <see cref="S"/>.</summary>
    public static float Scale { get; private set; } = 1;

    public static void InitScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        Scale = g.DpiX / 96f;
    }

    public static int S(int v) => (int)Math.Round(v * Scale);

    static readonly Dictionary<(float, FontStyle), Font> UiFonts = new();
    static readonly Dictionary<float, Font> IconFonts = new();

    public static Font Ui(float size, FontStyle style = FontStyle.Regular)
    {
        if (!UiFonts.TryGetValue((size, style), out var f))
            UiFonts[(size, style)] = f = new Font("Segoe UI", size, style);
        return f;
    }

    public static Font Icon(float size)
    {
        if (!IconFonts.TryGetValue(size, out var f))
            IconFonts[size] = f = new Font("Segoe MDL2 Assets", size);
        return f;
    }

    public static void DrawGlyph(Graphics g, string glyph, Rectangle r, Color c, float size) =>
        TextRenderer.DrawText(g, glyph, Icon(size), r, c,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Green circle with a white tick (online / ready) or a red dot (offline).</summary>
    public static void DrawStatus(Graphics g, Rectangle r, bool online)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (online)
        {
            using var b = new SolidBrush(Online);
            g.FillEllipse(b, r);
            using var pen = new Pen(Color.White, Math.Max(1.6f, r.Width / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(r.X + r.Width * 0.27f, r.Y + r.Height * 0.52f),
                new PointF(r.X + r.Width * 0.44f, r.Y + r.Height * 0.68f),
                new PointF(r.X + r.Width * 0.74f, r.Y + r.Height * 0.34f),
            });
        }
        else
        {
            using var b = new SolidBrush(Red);
            g.FillEllipse(b, r);
        }
    }

    /// <summary>Round avatar with the first letter of a name, coloured from the name.</summary>
    public static void DrawAvatar(Graphics g, Rectangle r, string name)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = new[] { Red, Orange, Blue2, Green, Color.FromArgb(0x8E, 0x44, 0xAD), Color.FromArgb(0x16, 0xA0, 0x85) };
        int h = 0;
        foreach (var c in name ?? "") h = h * 31 + c;
        using var b = new SolidBrush(palette[Math.Abs(h) % palette.Length]);
        g.FillRectangle(b, r);
        var letter = string.IsNullOrEmpty(name) ? "?" : char.ToUpper(name[0]).ToString();
        TextRenderer.DrawText(g, letter, Ui(r.Height * 0.38f / Scale, FontStyle.Bold), r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

public static class Glyph
{
    public const string ZoomIn = "", ZoomOut = "";
    public const string Chat = "", Monitor = "", Bolt = "", Keyboard = "",
        Clipboard = "", Folder = "", Cancel = "", ChromeClose = "",
        ChromeMin = "", ChromeMax = "", ChromeRestore = "", Star = "",
        StarFill = "", History = "", Bookmarks = "", Menu = "", Forward = "",
        Info = "", FullScreen = "", BackToWindow = "", Lock = "", Camera = "",
        Refresh = "", Settings = "", Add = "", Send = "", Upload = "",
        Download = "", Up = "", Contact = "", Document = "", Power = "",
        More = "", Copy = "", Mouse = "", FileExchange = "", Search = "",
        Shield = "", Back = "", Globe = "";
}

public static class Logo
{
    /// <summary>
    /// RemoteCraft mark: two overlapping rounded diamonds (two desks), red and orange.
    /// Must stay in sync with installer\make-icon.ps1.
    /// </summary>
    public static void Draw(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height);
        float side = s * 0.50f;
        var c1 = new PointF(r.X + s * 0.36f, r.Y + s * 0.5f);
        var c2 = new PointF(r.X + s * 0.64f, r.Y + s * 0.5f);
        using (var b = new SolidBrush(Theme.Red)) FillDiamond(g, b, c1, side, s * 0.07f);
        using (var b = new SolidBrush(Color.FromArgb(235, Theme.Orange))) FillDiamond(g, b, c2, side, s * 0.07f);
        using var w = new SolidBrush(Color.White);
        FillDiamond(g, w, c2, side * 0.36f, s * 0.02f);
    }

    static void FillDiamond(Graphics g, Brush b, PointF c, float side, float radius)
    {
        using var path = Theme.Rounded(new RectangleF(-side / 2, -side / 2, side, side), radius);
        var state = g.Save();
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform(45);
        g.FillPath(b, path);
        g.Restore(state);
    }

    static Icon _icon;
    public static Icon AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            using var s = typeof(Logo).Assembly.GetManifestResourceStream("RemoteCraft.ico");
            return _icon = s != null ? new Icon(s) : SystemIcons.Application;
        }
    }
}
