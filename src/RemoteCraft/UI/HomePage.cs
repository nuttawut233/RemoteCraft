using System.Drawing.Drawing2D;

namespace RemoteCraft.UI;

/// <summary>The "New Connection" page: This Desk sidebar, Remote Desk card, Discovery, recent sessions.</summary>
public sealed class HomePage : Panel
{
    readonly MainForm _main;
    readonly Panel _sidebar, _scroll;
    readonly Label _idLabel, _statusText, _statusDetail;
    readonly Panel _idBox, _statusIcon;
    readonly LinkLabel _passwordLink;
    readonly GradientPanel _install, _discovery;
    readonly Panel _remoteCard;
    readonly BoxedTextBox _address;
    readonly List<Control> _sectionControls = new();
    bool _statusOk;
    bool _showAll;

    public HomePage(MainForm main)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;

        // ---------- left: This Desk ----------
        _sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(300), BackColor = Color.White };
        _sidebar.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Line), _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);

        var thisDesk = Ctl.Label("This Desk", 18, Theme.Heading);
        thisDesk.Location = new Point(Theme.S(22), Theme.S(22));
        var info = new IconButton(Glyph.Info, "How others can reach this desk") { Location = new Point(Theme.S(250), Theme.S(26)), Size = new Size(Theme.S(30), Theme.S(30)), GlyphColor = Color.FromArgb(0xAA, 0xAA, 0xAA), GlyphSize = 13 };
        info.Click += (_, _) => _main.ShowDeskInfo();
        var desc = Ctl.Label("Your Desk can be accessed with this address.", 10.5f, Theme.SubText, autoSize: false);
        desc.SetBounds(Theme.S(22), Theme.S(68), Theme.S(260), Theme.S(44));

        _idBox = new Panel { Bounds = new Rectangle(Theme.S(24), Theme.S(116), Theme.S(250), Theme.S(42)), BackColor = Color.White, Cursor = Cursors.Hand };
        _idBox.Paint += (_, e) =>
        {
            e.Graphics.DrawRectangle(new Pen(Color.FromArgb(0xC8, 0xC8, 0xC8)), 0, 0, _idBox.Width - 1, _idBox.Height - 1);
            Theme.DrawAvatar(e.Graphics, new Rectangle(Theme.S(4), Theme.S(4), Theme.S(34), Theme.S(34)), Settings.Current.DisplayName);
        };
        _idLabel = new Label { Font = Theme.Ui(14.5f, FontStyle.Bold), ForeColor = Color.Black, AutoSize = false, Bounds = new Rectangle(Theme.S(44), Theme.S(4), Theme.S(200), Theme.S(34)), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
        _idBox.Controls.Add(_idLabel);
        var idMenu = Ctl.Menu();
        idMenu.Items.Add(Ctl.Item("Copy address", () => Clipboard.SetText(Settings.Current.DeskId), Glyph.Copy));
        idMenu.Items.Add(Ctl.Item("Change display name…", () => _main.ShowSettings("General"), Glyph.Contact));
        _idBox.ContextMenuStrip = _idLabel.ContextMenuStrip = idMenu;
        _idLabel.DoubleClick += (_, _) => { Clipboard.SetText(Settings.Current.DeskId); _main.Toast("Address copied to the clipboard."); };

        _passwordLink = Ctl.Link("", Theme.Link);
        _passwordLink.SetBounds(Theme.S(48), Theme.S(168), Theme.S(240), Theme.S(50));
        _passwordLink.LinkClicked += (_, _) => _main.SetPasswordDialog();

        _install = new GradientPanel { TopColor = Color.FromArgb(0xF5, 0x86, 0x3B), BottomColor = Color.FromArgb(0xE3, 0x3E, 0x33), Bounds = new Rectangle(0, Theme.S(238), Theme.S(299), Theme.S(142)), Mode = LinearGradientMode.Horizontal };
        var installTitle = Ctl.Label("Install RemoteCraft", 13.5f, Color.White, FontStyle.Bold);
        installTitle.Location = new Point(Theme.S(22), Theme.S(26));
        var installLink = Ctl.Link("Install RemoteCraft on this computer…", Color.White);
        installLink.SetBounds(Theme.S(22), Theme.S(70), Theme.S(260), Theme.S(50));
        installLink.LinkClicked += (_, _) => _main.Install();
        _install.Controls.AddRange(new Control[] { installTitle, installLink });

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Color.White };
        _statusIcon = new Panel { Bounds = new Rectangle(Theme.S(22), Theme.S(12), Theme.S(20), Theme.S(20)), BackColor = Color.White };
        _statusIcon.Paint += (_, e) => Theme.DrawStatus(e.Graphics, new Rectangle(0, 0, Theme.S(19), Theme.S(19)), _statusOk);
        _statusText = Ctl.Label("", 10.5f, Theme.Text);
        _statusText.Location = new Point(Theme.S(48), Theme.S(11));
        _statusDetail = Ctl.Label("", 8.5f, Theme.SubText, autoSize: false);
        _statusDetail.SetBounds(Theme.S(48), Theme.S(33), Theme.S(240), Theme.S(30));
        statusPanel.Controls.AddRange(new Control[] { _statusIcon, _statusText, _statusDetail });

        _sidebar.Controls.AddRange(new Control[] { thisDesk, info, desc, _idBox, _passwordLink, _install, statusPanel });

        // ---------- right ----------
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };

        _remoteCard = new Panel { Bounds = new Rectangle(Theme.S(28), Theme.S(28), Theme.S(600), Theme.S(226)), BackColor = Color.White };
        _remoteCard.Paint += (_, e) =>
        {
            using var b = new SolidBrush(Theme.Orange);
            e.Graphics.FillRectangle(b, 0, 0, 6, _remoteCard.Height);
        };
        var rdTitle = Ctl.Label("Remote Desk", 17, Theme.Heading);
        rdTitle.Location = new Point(Theme.S(28), Theme.S(20));
        var rdDesc = Ctl.Label("Please enter the address of the remote desk you would like to access.", 10.5f, Theme.SubText);
        rdDesc.Location = new Point(Theme.S(30), Theme.S(70));
        var rdInfo = new IconButton(Glyph.Info, "A desk id (e.g. 123 456 789), or an IP address / host name[:port]") { Size = new Size(Theme.S(26), Theme.S(26)), GlyphColor = Color.FromArgb(0xAA, 0xAA, 0xAA), GlyphSize = 12 };
        rdInfo.Location = new Point(Theme.S(560), Theme.S(66));
        _address = new BoxedTextBox("Enter Remote Desk ID or Alias", 12) { Bounds = new Rectangle(Theme.S(30), Theme.S(108), Theme.S(556), Theme.S(42)) };
        _address.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _main.Connect(_address.Text, "control"); }
        };
        var browse = FlatButton.GrayBtn("Browse Files");
        browse.Bounds = new Rectangle(Theme.S(232), Theme.S(166), Theme.S(170), Theme.S(42));
        browse.Click += (_, _) => _main.Connect(_address.Text, "files");
        var connect = FlatButton.Green("Connect");
        connect.Bounds = new Rectangle(Theme.S(416), Theme.S(166), Theme.S(170), Theme.S(42));
        connect.Click += (_, _) => _main.Connect(_address.Text, "control");
        _remoteCard.Controls.AddRange(new Control[] { rdTitle, rdDesc, rdInfo, _address, browse, connect });

        _discovery = new GradientPanel { TopColor = Theme.Blue1, BottomColor = Theme.Blue2, Bounds = new Rectangle(Theme.S(642), Theme.S(28), Theme.S(300), Theme.S(226)) };
        var dTitle = Ctl.Label("Discovery", 14.5f, Color.White, FontStyle.Bold);
        dTitle.Location = new Point(Theme.S(24), Theme.S(26));
        var dText = Ctl.Label("Find other RemoteCraft clients in your local network automatically.", 11, Color.White, autoSize: false);
        dText.SetBounds(Theme.S(24), Theme.S(70), Theme.S(255), Theme.S(90));
        var dClose = Ctl.Link("Close", Color.White, 11);
        dClose.TextAlign = ContentAlignment.MiddleRight;
        dClose.SetBounds(Theme.S(180), Theme.S(172), Theme.S(96), Theme.S(28));
        dClose.LinkClicked += (_, _) =>
        {
            Settings.Current.DiscoveryCardClosed = true;
            Settings.Current.Save();
            LayoutRight();
        };
        _discovery.Controls.AddRange(new Control[] { dTitle, dText, dClose });

        _scroll.Controls.AddRange(new Control[] { _remoteCard, _discovery });
        _scroll.Resize += (_, _) => LayoutRight();

        Controls.Add(_scroll);
        Controls.Add(_sidebar);
        RefreshDesk();
        RefreshSessions();
    }

    public string AddressText { get => _address.Text; set => _address.Text = value; }
    public void FocusAddress() => _address.Box.Focus();

    public void RefreshDesk()
    {
        var s = Settings.Current;
        _idLabel.Text = DeskId.Format(s.DeskId);
        _idBox.Invalidate();
        _passwordLink.Text = s.HasPassword ? "Change password for unattended access…" : "Set password for unattended access…";
        _install.Visible = !Installer.IsInstalled && !s.InstallCardHidden;
    }

    public void SetStatus(bool ok, string text, string detail)
    {
        _statusOk = ok;
        _statusText.Text = text;
        _statusDetail.Text = detail;
        _statusIcon.Invalidate();
    }

    public void RefreshSessions()
    {
        _scroll.SuspendLayout();
        foreach (var c in _sectionControls) { _scroll.Controls.Remove(c); c.Dispose(); }
        _sectionControls.Clear();

        var s = Settings.Current;
        var disc = _main.Discovery;
        var style = s.ViewMode switch { "List" => CardStyle.Row, "Compact" => CardStyle.Compact, _ => CardStyle.Tile };
        int rowWidth = Math.Max(Theme.S(400), _scroll.ClientSize.Width - Theme.S(60));

        // Discovered desks that aren't already in the recent list.
        var recentIds = s.Recent.Select(r => r.Id).ToHashSet();
        var peers = s.DiscoveryEnabled && disc != null ? disc.Peers.Where(p => !recentIds.Contains(p.Id)).ToList() : new();
        if (peers.Count > 0)
        {
            AddHeader("DISCOVERED", false);
            var flow = NewFlow();
            foreach (var p in peers)
            {
                var card = new SessionCard(style, p.Id, string.IsNullOrEmpty(p.Name) ? DeskId.Format(p.Id) : p.Name,
                    DeskId.Format(p.Id) + "  ·  " + p.User, true, false, rowWidth) { ShowStar = false };
                card.Activated += c => _main.Connect(c.Address, "control");
                card.MenuRequested += (c, pt) => ShowCardMenu(c, pt, null);
                flow.Controls.Add(card);
            }
            AddFlow(flow);
        }

        AddHeader("RECENT SESSIONS", true);
        var list = s.Recent.OrderByDescending(r => r.Favorite).ThenByDescending(r => r.LastUsed).ToList();
        if (!_showAll) list = list.Take(style == CardStyle.Row ? 20 : 12).ToList();
        if (list.Count == 0)
        {
            var empty = Ctl.Label("No sessions yet. Enter the address of a remote desk above to connect.", 10.5f, Theme.SubText);
            AddFlowControl(empty);
        }
        else
        {
            var flow = NewFlow();
            foreach (var r in list)
            {
                bool online = disc?.IsOnline(r.Id) ?? false;
                var sub = !string.IsNullOrEmpty(r.Alias) ? DeskId.Format(r.Id) : r.Name;
                if (style == CardStyle.Row) sub = $"{r.Name}   ·   {r.LastUsed:g}";
                var card = new SessionCard(style, r.Id, r.Title, sub, online, r.Favorite, rowWidth);
                card.Activated += c => _main.Connect(c.Address, "control");
                card.StarToggled += c => { r.Favorite = c.Favorite; s.Save(); RefreshSessions(); };
                card.MenuRequested += (c, pt) => ShowCardMenu(c, pt, r);
                flow.Controls.Add(card);
            }
            AddFlow(flow);
        }
        _scroll.ResumeLayout();
        LayoutRight();
    }

    void ShowCardMenu(SessionCard card, Point pt, RecentSession r)
    {
        var m = Ctl.Menu();
        m.Items.Add(Ctl.Item("Connect", () => _main.Connect(card.Address, "control"), Glyph.Monitor));
        m.Items.Add(Ctl.Item("Browse files", () => _main.Connect(card.Address, "files"), Glyph.Folder));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Ctl.Item("Copy address", () => Clipboard.SetText(card.Address), Glyph.Copy));
        if (r != null)
        {
            m.Items.Add(Ctl.Item("Rename (alias)…", () =>
            {
                var alias = Prompt.Show(FindForm(), "Alias", "Name shown for " + DeskId.Format(r.Id) + ":", r.Alias ?? r.Name ?? "");
                if (alias == null) return;
                r.Alias = alias.Trim();
                Settings.Current.Save();
                RefreshSessions();
            }, Glyph.Document));
            m.Items.Add(Ctl.Item(r.Favorite ? "Remove from favourites" : "Add to favourites", () =>
            {
                r.Favorite = !r.Favorite;
                Settings.Current.Save();
                RefreshSessions();
            }, Glyph.Star));
            m.Items.Add(Ctl.Item("Forget saved password", () =>
            {
                Settings.Current.SavedKeys.Remove(r.Id);
                Settings.Current.Save();
            }, Glyph.Lock));
            m.Items.Add(Ctl.Item("Remove from recent sessions", () =>
            {
                Settings.Current.Recent.Remove(r);
                Settings.Current.Save();
                try { File.Delete(AppPaths.ThumbFor(r.Id)); } catch { }
                RefreshSessions();
            }, Glyph.Cancel));
        }
        m.Show(card, pt);
    }

    FlowLayoutPanel NewFlow() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    void AddFlow(FlowLayoutPanel flow) => AddFlowControl(flow);

    void AddFlowControl(Control c)
    {
        c.Tag = "flow";
        _sectionControls.Add(c);
        _scroll.Controls.Add(c);
    }

    void AddHeader(string text, bool withViews)
    {
        var header = new Panel { Height = Theme.S(34), BackColor = Color.Transparent, Tag = "header" };
        var label = Ctl.Label(text, 10, Theme.SubText);
        label.Location = new Point(0, Theme.S(8));
        header.Controls.Add(label);
        if (withViews)
        {
            int x = TextRenderer.MeasureText(text, label.Font).Width + Theme.S(8);
            var all = Ctl.Link(_showAll ? "Show less" : "Show all", Theme.SubText, 10);
            all.SetBounds(x, Theme.S(8), Theme.S(80), Theme.S(22));
            all.LinkClicked += (_, _) => { _showAll = !_showAll; RefreshSessions(); };
            header.Controls.Add(all);
            x += Theme.S(84);
            foreach (var (mode, tip) in new[] { ("Tiles", "Tiles"), ("List", "List"), ("Compact", "Compact tiles") })
            {
                var b = new ViewModeButton(mode, tip) { Location = new Point(x, Theme.S(4)), Checked = Settings.Current.ViewMode == mode };
                b.Click += (_, _) =>
                {
                    Settings.Current.ViewMode = mode;
                    Settings.Current.Save();
                    RefreshSessions();
                };
                header.Controls.Add(b);
                x += Theme.S(36);
            }
        }
        _sectionControls.Add(header);
        _scroll.Controls.Add(header);
    }

    void LayoutRight()
    {
        var s = Settings.Current;
        int left = Theme.S(28) + _scroll.AutoScrollPosition.X;
        int y0 = _scroll.AutoScrollPosition.Y;
        _remoteCard.Location = new Point(left, Theme.S(28) + y0);
        _discovery.Visible = !s.DiscoveryCardClosed;
        _discovery.Location = new Point(left + Theme.S(614), Theme.S(28) + y0);

        int width = Math.Max(Theme.S(400), _scroll.ClientSize.Width - Theme.S(56));
        int y = Theme.S(290) + y0;
        foreach (var c in _sectionControls)
        {
            if ((string)c.Tag == "header")
            {
                c.SetBounds(left, y, width, Theme.S(34));
                y += Theme.S(40);
            }
            else
            {
                if (c is FlowLayoutPanel flow) flow.MaximumSize = new Size(width + Theme.S(12), 0);
                c.Location = new Point(left, y);
                y += c.Height + Theme.S(18);
            }
        }
        _scroll.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) => base.OnPaint(e);

    /// <summary>Draws the tiles / list / compact view-mode icons.</summary>
    sealed class ViewModeButton : IconButton
    {
        readonly string _mode;

        public ViewModeButton(string mode, string tip) : base("", tip)
        {
            _mode = mode;
            Size = new Size(Theme.S(30), Theme.S(26));
            CheckedColor = Color.FromArgb(0xDD, 0xDD, 0xDD);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            var c = Checked ? Color.FromArgb(0x22, 0x22, 0x22) : Color.FromArgb(0x88, 0x88, 0x88);
            using var b = new SolidBrush(c);
            int x = (Width - 16) / 2, y = (Height - 16) / 2;
            switch (_mode)
            {
                case "Tiles":
                    g.FillRectangle(b, x, y, 7, 7); g.FillRectangle(b, x + 9, y, 7, 7);
                    g.FillRectangle(b, x, y + 9, 7, 7); g.FillRectangle(b, x + 9, y + 9, 7, 7);
                    break;
                case "List":
                    for (int i = 0; i < 4; i++) { g.FillRectangle(b, x, y + i * 4 + 1, 3, 2); g.FillRectangle(b, x + 5, y + i * 4 + 1, 11, 2); }
                    break;
                default:
                    for (int i = 0; i < 4; i++) { g.FillRectangle(b, x, y + i * 4 + 1, 7, 2); g.FillRectangle(b, x + 9, y + i * 4 + 1, 7, 2); }
                    break;
            }
        }
    }
}
