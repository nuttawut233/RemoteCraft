using System.Drawing.Drawing2D;

namespace RemoteCraft.UI;

public enum CardStyle { Tile, Compact, Row }

/// <summary>A recent / discovered desk: thumbnail, online state, favourite star, id and ⋮ menu.</summary>
public sealed class SessionCard : Control
{
    readonly CardStyle _style;
    Image _thumb;
    string _hot;
    Rectangle _star, _menu, _thumbRect, _footer;

    public string Address { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool Online { get; }
    public bool Favorite { get; set; }
    public bool ShowStar { get; init; } = true;

    public event Action<SessionCard> Activated;
    public event Action<SessionCard> StarToggled;
    public event Action<SessionCard, Point> MenuRequested;

    public SessionCard(CardStyle style, string address, string title, string subtitle, bool online, bool favorite, int rowWidth = 600)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _style = style;
        Address = address;
        Title = title;
        Subtitle = subtitle;
        Online = online;
        Favorite = favorite;
        Cursor = Cursors.Hand;
        Margin = style == CardStyle.Row ? new Padding(0, 0, 0, Theme.S(6)) : new Padding(0, 0, Theme.S(12), Theme.S(12));
        Size = style switch
        {
            CardStyle.Tile => new Size(Theme.S(292), Theme.S(224)),
            CardStyle.Compact => new Size(Theme.S(196), Theme.S(156)),
            _ => new Size(rowWidth, Theme.S(46)),
        };
        _thumb = LoadThumb(address);
    }

    static Image LoadThumb(string address)
    {
        try
        {
            var path = AppPaths.ThumbFor(address);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumb?.Dispose();
        base.Dispose(disposing);
    }

    void DoLayout()
    {
        if (_style == CardStyle.Row)
        {
            _thumbRect = new Rectangle(Theme.S(40), Theme.S(5), Theme.S(64), Theme.S(36));
            _menu = new Rectangle(Width - Theme.S(34), Theme.S(8), Theme.S(28), Theme.S(30));
            _star = new Rectangle(Width - Theme.S(66), Theme.S(8), Theme.S(28), Theme.S(30));
            _footer = Rectangle.Empty;
            return;
        }
        int footer = Theme.S(_style == CardStyle.Tile ? 60 : 44);
        _thumbRect = new Rectangle(Theme.S(1), Theme.S(1), Width - 2, Height - footer - 1);
        _footer = new Rectangle(Theme.S(1), Height - footer, Width - 2, footer - 1);
        _star = new Rectangle(Width - Theme.S(32), Theme.S(6), Theme.S(26), Theme.S(26));
        _menu = new Rectangle(Width - Theme.S(30), _footer.Y + (footer - Theme.S(30)) / 2, Theme.S(24), Theme.S(30));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DoLayout();
        var g = e.Graphics;
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        if (_style == CardStyle.Row) PaintRow(g);
        else PaintTile(g);

        using var pen = new Pen(_hot != null ? Color.FromArgb(0x9A, 0x9A, 0x9A) : Color.FromArgb(0xD2, 0xD2, 0xD2));
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    void PaintThumb(Graphics g, Rectangle r)
    {
        if (_thumb != null)
        {
            // cover-fit
            float scale = Math.Max((float)r.Width / _thumb.Width, (float)r.Height / _thumb.Height);
            float w = _thumb.Width * scale, h = _thumb.Height * scale;
            var state = g.Save();
            g.SetClip(r);
            g.DrawImage(_thumb, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            g.Restore(state);
        }
        else
        {
            using var b = new LinearGradientBrush(r, Color.FromArgb(0x1E, 0x9B, 0xF0), Color.FromArgb(0x10, 0x4B, 0xC4), LinearGradientMode.ForwardDiagonal);
            g.FillRectangle(b, r);
            Theme.DrawGlyph(g, Glyph.Monitor, r, Color.FromArgb(150, Color.White), Math.Max(10, r.Height / 4f / Theme.Scale));
        }
    }

    void PaintTile(Graphics g)
    {
        PaintThumb(g, _thumbRect);
        Theme.DrawStatus(g, new Rectangle(Theme.S(8), Theme.S(8), Theme.S(18), Theme.S(18)), Online);
        if (ShowStar && (Favorite || _hot != null))
        {
            var starColor = Favorite ? Color.FromArgb(0xFF, 0xC1, 0x07) : Color.White;
            Theme.DrawGlyph(g, Favorite ? Glyph.StarFill : Glyph.Star, _star, starColor, 13);
        }
        else if (ShowStar)
            Theme.DrawGlyph(g, Glyph.Star, _star, Color.FromArgb(200, Color.White), 13);

        var textRect = new Rectangle(_footer.X + Theme.S(12), _footer.Y, _footer.Width - Theme.S(48), _footer.Height);
        if (!string.IsNullOrEmpty(Subtitle) && _style == CardStyle.Tile)
        {
            TextRenderer.DrawText(g, Title, Theme.Ui(10.5f), new Rectangle(textRect.X, textRect.Y + Theme.S(8), textRect.Width, Theme.S(22)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Subtitle, Theme.Ui(9f), new Rectangle(textRect.X, textRect.Y + Theme.S(30), textRect.Width, Theme.S(20)), Theme.SubText, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        else
            TextRenderer.DrawText(g, Title, Theme.Ui(_style == CardStyle.Tile ? 10.5f : 9.5f), textRect, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        DrawDots(g, _menu, _hot == "menu");
    }

    void PaintRow(Graphics g)
    {
        if (_hot != null) { using var hb = new SolidBrush(Color.FromArgb(0xF6, 0xF6, 0xF6)); g.FillRectangle(hb, ClientRectangle); }
        Theme.DrawStatus(g, new Rectangle(Theme.S(12), Theme.S(14), Theme.S(18), Theme.S(18)), Online);
        PaintThumb(g, _thumbRect);
        TextRenderer.DrawText(g, Title, Theme.Ui(10.5f), new Rectangle(Theme.S(118), 0, Theme.S(260), Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Subtitle ?? "", Theme.Ui(9.5f), new Rectangle(Theme.S(390), 0, Math.Max(10, _star.X - Theme.S(400)), Height), Theme.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (ShowStar)
            Theme.DrawGlyph(g, Favorite ? Glyph.StarFill : Glyph.Star, _star, Favorite ? Color.FromArgb(0xFF, 0xC1, 0x07) : Theme.SubText, 12);
        DrawDots(g, _menu, _hot == "menu");
    }

    static void DrawDots(Graphics g, Rectangle r, bool hot)
    {
        if (hot) { using var hb = new SolidBrush(Theme.Hover); g.FillRectangle(hb, r); }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Theme.SubText);
        int cx = r.X + r.Width / 2;
        for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx - 2, r.Y + r.Height / 2 + i * 6 - 2, 4, 4);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = _menu.Contains(e.Location) ? "menu" : ShowStar && _star.Contains(e.Location) ? "star" : "card";
        if (h != _hot) { _hot = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { MenuRequested?.Invoke(this, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (_menu.Contains(e.Location)) MenuRequested?.Invoke(this, new Point(_menu.X, _menu.Bottom));
        else if (ShowStar && _star.Contains(e.Location)) { Favorite = !Favorite; Invalidate(); StarToggled?.Invoke(this); }
        else Activated?.Invoke(this);
    }
}
