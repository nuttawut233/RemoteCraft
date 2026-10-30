namespace RemoteCraft.UI;

public sealed class TabItem
{
    public string Title { get; set; }
    public string Glyph { get; set; } = RemoteCraft.UI.Glyph.Monitor;
    public Control Page { get; set; }
    public bool Closable { get; set; } = true;
    public Color? Dot { get; set; }
    internal Rectangle Bounds, CloseBounds;
}

/// <summary>Custom window chrome: logo, document tabs, "+" and the min/max/close buttons.</summary>
public sealed class TitleBar : Control
{
    static readonly int LogoWidth = Theme.S(150), PlusWidth = Theme.S(40), BtnWidth = Theme.S(46);
    readonly Form _form;
    Rectangle _plus, _min, _max, _close;
    string _hot;

    public List<TabItem> Tabs { get; } = new();
    public TabItem Selected { get; private set; }

    public event Action<TabItem> TabSelected;
    public event Action<TabItem> TabCloseRequested;
    public event Action PlusClicked;
    public event Action LogoClicked;

    public TitleBar(Form form)
    {
        _form = form;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Theme.S(40);
        Dock = DockStyle.Top;
        BackColor = Theme.Chrome;
    }

    public void Add(TabItem t, bool select = true)
    {
        Tabs.Add(t);
        if (select) Select(t);
        else Invalidate();
    }

    public void Remove(TabItem t)
    {
        int i = Tabs.IndexOf(t);
        Tabs.Remove(t);
        if (Selected == t && Tabs.Count > 0) Select(Tabs[Math.Clamp(i - 1, 0, Tabs.Count - 1)]);
        Invalidate();
    }

    public void Select(TabItem t)
    {
        Selected = t;
        Invalidate();
        TabSelected?.Invoke(t);
    }

    void DoLayout(Graphics g)
    {
        int x = LogoWidth;
        int avail = Width - LogoWidth - PlusWidth - BtnWidth * 3 - 40;
        int each = Tabs.Count == 0 ? 0 : Math.Min(Theme.S(230), Math.Max(Theme.S(90), avail / Tabs.Count));
        foreach (var t in Tabs)
        {
            t.Bounds = new Rectangle(x, Theme.S(4), each, Height - 4);
            t.CloseBounds = t.Closable ? new Rectangle(t.Bounds.Right - Theme.S(26), t.Bounds.Y + (t.Bounds.Height - Theme.S(20)) / 2, Theme.S(20), Theme.S(20)) : Rectangle.Empty;
            x += each;
        }
        _plus = new Rectangle(x + 2, Theme.S(6), Theme.S(32), Height - 10);
        _close = new Rectangle(Width - BtnWidth, 0, BtnWidth, Height);
        _max = new Rectangle(Width - BtnWidth * 2, 0, BtnWidth, Height);
        _min = new Rectangle(Width - BtnWidth * 3, 0, BtnWidth, Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        DoLayout(g);
        g.Clear(BackColor);

        // logo "tab"
        Logo.Draw(g, new RectangleF(Theme.S(10), Theme.S(8), Theme.S(24), Theme.S(24)));
        TextRenderer.DrawText(g, "RemoteCraft", Theme.Ui(11f, FontStyle.Bold), new Rectangle(Theme.S(38), 0, LogoWidth - Theme.S(38), Height), _hot == "logo" ? Theme.Red : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

        using var line = new Pen(Theme.Line);
        foreach (var t in Tabs)
        {
            var r = t.Bounds;
            bool sel = t == Selected;
            if (sel)
            {
                g.FillRectangle(Brushes.White, r);
                g.DrawLine(line, r.X, r.Y, r.Right - 1, r.Y);
                g.DrawLine(line, r.X, r.Y, r.X, r.Bottom);
                g.DrawLine(line, r.Right - 1, r.Y, r.Right - 1, r.Bottom);
            }
            else if (_hot == "tab:" + Tabs.IndexOf(t))
            {
                using var hb = new SolidBrush(Theme.Hover);
                g.FillRectangle(hb, r);
            }
            Theme.DrawGlyph(g, t.Glyph, new Rectangle(r.X + Theme.S(8), r.Y, Theme.S(22), r.Height), Theme.Text, 12);
            if (t.Dot is Color dot)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var db = new SolidBrush(dot);
                g.FillEllipse(db, r.X + Theme.S(22), r.Y + r.Height / 2 + 1, Theme.S(8), Theme.S(8));
            }
            var textRect = new Rectangle(r.X + Theme.S(34), r.Y, r.Width - Theme.S(34) - Theme.S(t.Closable ? 28 : 6), r.Height);
            TextRenderer.DrawText(g, t.Title, Theme.Ui(10.5f), textRect, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (t.Closable)
            {
                bool hot = _hot == "close:" + Tabs.IndexOf(t);
                if (hot) { using var hb = new SolidBrush(Theme.Hover); g.FillRectangle(hb, t.CloseBounds); }
                Theme.DrawGlyph(g, Glyph.Cancel, t.CloseBounds, hot ? Theme.Red : Theme.SubText, 8);
            }
        }

        if (_hot == "plus") { using var hb = new SolidBrush(Theme.Hover); g.FillRectangle(hb, _plus); }
        Theme.DrawGlyph(g, Glyph.Add, _plus, Theme.SubText, 11);

        void Btn(Rectangle r, string glyph, string key, bool danger = false)
        {
            if (_hot == key)
            {
                using var hb = new SolidBrush(danger ? Color.FromArgb(0xE8, 0x11, 0x23) : Theme.Hover);
                g.FillRectangle(hb, r);
            }
            Theme.DrawGlyph(g, glyph, r, _hot == key && danger ? Color.White : Theme.Text, 9);
        }
        Btn(_min, Glyph.ChromeMin, "min");
        Btn(_max, _form.WindowState == FormWindowState.Maximized ? Glyph.ChromeRestore : Glyph.ChromeMax, "max");
        Btn(_close, Glyph.ChromeClose, "close", true);

        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        if (Selected != null)
            g.DrawLine(Pens.White, Selected.Bounds.X + 1, Height - 1, Selected.Bounds.Right - 2, Height - 1);
    }

    string HitTest(Point p)
    {
        if (_close.Contains(p)) return "close";
        if (_max.Contains(p)) return "max";
        if (_min.Contains(p)) return "min";
        if (_plus.Contains(p)) return "plus";
        if (p.X < LogoWidth) return "logo";
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].CloseBounds.Contains(p)) return "close:" + i;
            if (Tabs[i].Bounds.Contains(p)) return "tab:" + i;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitTest(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = null;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var h = HitTest(e.Location);
        if (h == null)
        {
            if (e.Clicks == 2) ToggleMax();
            else
            {
                Native.ReleaseCapture();
                Native.SendMessage(_form.Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
            }
        }
        else if (h.StartsWith("tab:")) Select(Tabs[int.Parse(h[4..])]);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var h = HitTest(e.Location);
        if (e.Button == MouseButtons.Middle && h != null && h.StartsWith("tab:"))
        {
            var t = Tabs[int.Parse(h[4..])];
            if (t.Closable) TabCloseRequested?.Invoke(t);
            return;
        }
        if (e.Button != MouseButtons.Left || h == null) return;
        switch (h)
        {
            case "close": _form.Close(); break;
            case "min": _form.WindowState = FormWindowState.Minimized; break;
            case "max": ToggleMax(); break;
            case "plus": PlusClicked?.Invoke(); break;
            case "logo": LogoClicked?.Invoke(); break;
            default:
                if (h.StartsWith("close:")) TabCloseRequested?.Invoke(Tabs[int.Parse(h[6..])]);
                break;
        }
    }

    void ToggleMax()
    {
        ((MainForm)_form).ToggleMaximize();
        Invalidate();
    }
}
