using System.Drawing.Drawing2D;

namespace RemoteCraft.UI;

/// <summary>Flat filled button (Connect / Browse Files style).</summary>
public class FlatButton : Control
{
    bool _hover, _down;
    public Color Fill { get; set; } = Theme.Gray;
    public Color FillHover { get; set; } = Theme.GrayHover;
    public Color TextColor { get; set; } = Color.Black;
    public string Icon { get; set; }

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Ui(10.5f, FontStyle.Bold);
        Size = new Size(Theme.S(120), Theme.S(36));
    }

    public static FlatButton Green(string text) => new() { Text = text, Fill = Theme.Green, FillHover = Theme.GreenHover, TextColor = Color.White };
    public static FlatButton GrayBtn(string text) => new() { Text = text };
    public static FlatButton RedBtn(string text) => new() { Text = text, Fill = Theme.Red, FillHover = Color.FromArgb(0xD3, 0x35, 0x2D), TextColor = Color.White };

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var c = !Enabled ? Color.FromArgb(0xDD, 0xDD, 0xDD) : _hover || _down ? FillHover : Fill;
        g.Clear(Parent?.BackColor ?? Color.White);
        using (var b = new SolidBrush(c)) g.FillRectangle(b, ClientRectangle);
        var fc = Enabled ? TextColor : Color.Gray;
        if (!string.IsNullOrEmpty(Icon))
        {
            var sz = TextRenderer.MeasureText(Text, Font);
            int total = Theme.S(22) + sz.Width;
            int x = (Width - total) / 2;
            Theme.DrawGlyph(g, Icon, new Rectangle(x, 0, Theme.S(18), Height), fc, 11);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + Theme.S(22), 0, sz.Width + 4, Height), fc, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
        else
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Icon-only toolbar button drawn from Segoe MDL2 Assets.</summary>
public class IconButton : Control
{
    bool _hover;
    static readonly ToolTip Tips = new() { InitialDelay = 400 };

    public string Glyph { get; set; }
    public float GlyphSize { get; set; } = 13;
    public bool Checked { get; set; }
    public bool DropDown { get; set; }
    public Color GlyphColor { get; set; } = Theme.Text;
    public Color HoverColor { get; set; } = Theme.Hover;
    public Color CheckedColor { get; set; } = Color.FromArgb(0xD6, 0xD6, 0xD6);
    public string Tip { set => Tips.SetToolTip(this, value); }

    public IconButton(string glyph, string tip = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Glyph = glyph;
        Size = new Size(Theme.S(40), Theme.S(36));
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        if (tip != null) Tip = tip;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Checked) { using var b = new SolidBrush(CheckedColor); g.FillRectangle(b, ClientRectangle); }
        else if (_hover && Enabled) { using var b = new SolidBrush(HoverColor); g.FillRectangle(b, ClientRectangle); }
        var r = ClientRectangle;
        if (DropDown) r.Width -= Theme.S(10);
        var c = Enabled ? GlyphColor : Color.FromArgb(0xAA, 0xAA, 0xAA);
        Theme.DrawGlyph(g, Glyph, r, c, GlyphSize);
        if (DropDown)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width - Theme.S(10), cy = Height / 2 + 1;
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[] { new Point(cx - 3, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 2) });
        }
    }

    /// <summary>Shows a menu right under the button.</summary>
    public void ShowMenu(ContextMenuStrip menu) => menu.Show(this, new Point(0, Height));
}

/// <summary>A single-line text box with a 1-px border and placeholder, as used in the address fields.</summary>
public class BoxedTextBox : Panel
{
    public TextBox Box { get; }
    public Color BorderColor { get; set; } = Color.FromArgb(0xCC, 0xCC, 0xCC);

    public BoxedTextBox(string placeholder, float fontSize = 11)
    {
        BackColor = Color.White;
        Padding = new Padding(Theme.S(10), 0, Theme.S(6), 0);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Font = Theme.Ui(fontSize),
            ForeColor = Theme.Text,
            BackColor = Color.White,
        };
        Controls.Add(Box);
        Box.GotFocus += (_, _) => { BorderColor = Theme.Heading; Invalidate(); };
        Box.LostFocus += (_, _) => { BorderColor = Color.FromArgb(0xCC, 0xCC, 0xCC); Invalidate(); };
        Click += (_, _) => Box.Focus();
    }

    public override string Text { get => Box.Text; set => Box.Text = value; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Box?.SetBounds(Padding.Left, (Height - Box.PreferredHeight) / 2, Width - Padding.Horizontal, Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Panel with a vertical gradient fill (Discovery / Install cards).</summary>
public class GradientPanel : Panel
{
    public Color TopColor { get; set; }
    public Color BottomColor { get; set; }
    public LinearGradientMode Mode { get; set; } = LinearGradientMode.ForwardDiagonal;

    public GradientPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        using var b = new LinearGradientBrush(ClientRectangle, TopColor, BottomColor, Mode);
        e.Graphics.FillRectangle(b, ClientRectangle);
    }
}

public static class Ctl
{
    public static Label Label(string text, float size, Color color, FontStyle style = FontStyle.Regular, bool autoSize = true) => new()
    {
        Text = text,
        Font = Theme.Ui(size, style),
        ForeColor = color,
        AutoSize = autoSize,
        BackColor = Color.Transparent,
    };

    public static LinkLabel Link(string text, Color color, float size = 10.5f)
    {
        var l = new LinkLabel
        {
            Text = text,
            Font = Theme.Ui(size),
            LinkColor = color,
            ActiveLinkColor = color,
            VisitedLinkColor = color,
            AutoSize = false,
            BackColor = Color.Transparent,
            LinkBehavior = LinkBehavior.AlwaysUnderline,
        };
        return l;
    }

    public static ToolStripMenuItem Item(string text, Action onClick, string glyph = null)
    {
        var i = new ToolStripMenuItem(text);
        i.Click += (_, _) => onClick();
        if (glyph != null) i.Image = GlyphImage(glyph, Theme.Text);
        return i;
    }

    static readonly Dictionary<(string, Color), Bitmap> GlyphCache = new();

    public static Bitmap GlyphImage(string glyph, Color color, int size = 16)
    {
        if (GlyphCache.TryGetValue((glyph, color), out var bmp)) return bmp;
        bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var b = new SolidBrush(color);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, Theme.Icon(size * 0.6f), b, new RectangleF(0, 0, size, size), sf);
        }
        return GlyphCache[(glyph, color)] = bmp;
    }

    public static ContextMenuStrip Menu()
    {
        return new ContextMenuStrip { Font = Theme.Ui(10), ShowImageMargin = true, RenderMode = ToolStripRenderMode.System };
    }
}
