using System.Runtime.InteropServices;
using RemoteCraft.Client;
using RemoteCraft.Host;
using RemoteCraft.Net;

namespace RemoteCraft.UI;

public sealed class MainForm : Form
{
    readonly TitleBar _title;
    readonly Panel _addressBar, _content, _statusDot;
    readonly BoxedTextBox _addr;
    readonly HomePage _home;
    readonly TabItem _homeTab;
    readonly NotifyIcon _tray;
    readonly Label _toast;
    readonly System.Windows.Forms.Timer _toastTimer, _peerTimer;
    Action _toastAction;
    RelayLink _relay;
    bool _exiting, _trayHintShown, _startHidden;
    string _peerSignature = "";
    FormWindowState _prevState;
    Rectangle _prevBounds;

    public HostServer Host { get; private set; }
    public Discovery Discovery { get; private set; }
    public string RelayStatus => _relay?.Status;
    public bool IsFullscreen { get; private set; }

    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);

    public MainForm(bool startHidden)
    {
        Ui.Init();
        ClipboardHub.Init();
        _startHidden = startHidden;

        Text = "RemoteCraft";
        Icon = Logo.AppIcon;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Chrome;
        Padding = new Padding(Theme.S(4));
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(Theme.S(1360), Theme.S(860));
        MinimumSize = new Size(Theme.S(960), Theme.S(620));
        Font = Theme.Ui(10);
        DoubleBuffered = true;
        KeyPreview = true;

        _title = new TitleBar(this);
        _title.TabSelected += OnTabSelected;
        _title.TabCloseRequested += t => { if (t.Page is SessionPage sp) CloseSession(sp); };
        _title.PlusClicked += () => { _title.Select(_homeTab); _home.FocusAddress(); };
        _title.LogoClicked += () => ShowMainMenu(_title, new Point(Theme.S(8), _title.Height));

        // ---------- address bar ----------
        _addressBar = new Panel { Dock = DockStyle.Top, Height = Theme.S(50), BackColor = Color.FromArgb(0xF7, 0xF7, 0xF7) };
        _addressBar.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Line), 0, _addressBar.Height - 1, _addressBar.Width, _addressBar.Height - 1);
        _statusDot = new Panel { Bounds = new Rectangle(Theme.S(14), Theme.S(15), Theme.S(21), Theme.S(21)), BackColor = _addressBar.BackColor };
        _statusDot.Paint += (_, e) => Theme.DrawStatus(e.Graphics, new Rectangle(0, 0, Theme.S(20), Theme.S(20)), Host?.Port > 0);
        _addr = new BoxedTextBox("Enter Remote Desk ID or Alias", 11.5f) { Bounds = new Rectangle(Theme.S(46), Theme.S(7), Theme.S(500), Theme.S(36)) };
        _addr.Box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _addr.Box.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _addr.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Connect(_addr.Text, "control"); } };
        var go = new IconButton(Glyph.Forward, "Connect") { Bounds = new Rectangle(Theme.S(554), Theme.S(7), Theme.S(40), Theme.S(36)), GlyphSize = 14 };
        go.Click += (_, _) => Connect(_addr.Text, "control");
        var files = new IconButton(Glyph.FileExchange, "Browse files") { Bounds = new Rectangle(Theme.S(598), Theme.S(7), Theme.S(40), Theme.S(36)), GlyphSize = 14 };
        files.Click += (_, _) => Connect(_addr.Text, "files");
        var history = new IconButton(Glyph.History, "Recent sessions") { Size = new Size(Theme.S(44), Theme.S(36)), GlyphSize = 15 };
        history.Click += (_, _) => history.ShowMenu(BuildRecentMenu(false));
        var favs = new IconButton(Glyph.Bookmarks, "Favourites") { Size = new Size(Theme.S(44), Theme.S(36)), GlyphSize = 15 };
        favs.Click += (_, _) => favs.ShowMenu(BuildRecentMenu(true));
        var menu = new IconButton(Glyph.Menu, "Menu") { Size = new Size(Theme.S(44), Theme.S(36)), GlyphSize = 15 };
        menu.Click += (_, _) => ShowMainMenu(menu, new Point(menu.Width - Theme.S(220), menu.Height));
        _addressBar.Controls.AddRange(new Control[] { _statusDot, _addr, go, files, history, favs, menu });
        _addressBar.Resize += (_, _) =>
        {
            menu.Location = new Point(_addressBar.Width - Theme.S(56), Theme.S(7));
            favs.Location = new Point(_addressBar.Width - Theme.S(104), Theme.S(7));
            history.Location = new Point(_addressBar.Width - Theme.S(152), Theme.S(7));
        };

        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _home = new HomePage(this);
        _content.Controls.Add(_home);
        _homeTab = new TabItem { Title = "New Connection", Glyph = Glyph.Monitor, Page = _home, Closable = false };

        _toast = new Label
        {
            AutoSize = false,
            Visible = false,
            BackColor = Color.FromArgb(0x33, 0x33, 0x33),
            ForeColor = Color.White,
            Font = Theme.Ui(10),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(Theme.S(16), 0, Theme.S(16), 0),
        };
        _toast.Click += (_, _) => { _toast.Visible = false; _toastAction?.Invoke(); };
        _toastTimer = new System.Windows.Forms.Timer { Interval = 4500 };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visible = false; };
        Controls.Add(_toast);

        Controls.Add(_content);
        Controls.Add(_addressBar);
        Controls.Add(_title);
        _title.Add(_homeTab);

        _tray = new NotifyIcon { Icon = Logo.AppIcon, Text = "RemoteCraft", Visible = true };
        var trayMenu = Ctl.Menu();
        trayMenu.Items.Add(Ctl.Item("Open RemoteCraft", ShowMain, Glyph.Monitor));
        trayMenu.Items.Add(Ctl.Item("Copy my address", () => Clipboard.SetText(Settings.Current.DeskId), Glyph.Copy));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(Ctl.Item("Exit", ExitApp, Glyph.Power));
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowMain();

        _peerTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _peerTimer.Tick += (_, _) => CheckPeers();
        _peerTimer.Start();

        UpdateAutoComplete();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Host == null) StartNetwork();
    }

    // ---------- networking ----------

    void StartNetwork()
    {
        var s = Settings.Current;
        Host = new HostServer();
        Host.IncomingSession += session => new HostSessionForm(session).Show();
        Host.Start(s.Port);

        Discovery = new Discovery(() => (s.DeskId, s.DisplayName, Host?.Port ?? 0)) { Announce = s.DiscoveryEnabled };
        Discovery.Start();

        if (!string.IsNullOrWhiteSpace(s.RelayServer))
        {
            _relay = new RelayLink(s.RelayServer, s.DeskId, Host.HandleIncoming);
            _relay.StatusChanged += () => Ui.Post(UpdateStatus);
        }
        UpdateStatus();
    }

    public void RestartNetwork()
    {
        _relay?.Dispose();
        _relay = null;
        Discovery?.Dispose();
        Host?.Dispose();
        StartNetwork();
        _home.RefreshSessions();
    }

    void UpdateStatus()
    {
        var s = Settings.Current;
        bool ok = Host?.Port > 0;
        var detail = ok ? $"Port {Host.Port}" : "Not listening: " + Host?.ListenError;
        detail += s.DiscoveryEnabled ? "  ·  LAN discovery on" : "  ·  LAN discovery off";
        if (_relay != null) detail += "\n" + _relay.Status;
        _home.SetStatus(ok, ok ? "RemoteCraft ready." : "RemoteCraft is not reachable.", detail);
        _statusDot.Invalidate();
        _tray.Text = "RemoteCraft – " + DeskId.Format(s.DeskId);
    }

    void CheckPeers()
    {
        if (Discovery == null) return;
        var sig = string.Join(",", Discovery.Peers.Select(p => p.Id + p.Name));
        if (sig == _peerSignature) return;
        _peerSignature = sig;
        if (_title.Selected == _homeTab) _home.RefreshSessions();
    }

    // ---------- sessions ----------

    public void Connect(string address, string mode)
    {
        address = (address ?? "").Trim();
        if (address.Length == 0)
        {
            Toast("Enter the address of a remote desk first.");
            _home.FocusAddress();
            return;
        }
        // An alias from the recent list resolves to its address.
        var alias = Settings.Current.Recent.FirstOrDefault(r => string.Equals(r.Alias, address, StringComparison.OrdinalIgnoreCase));
        if (alias != null) address = alias.Id;
        if (DeskId.IsId(address)) address = DeskId.Normalize(address);

        foreach (var t in _title.Tabs)
            if (t.Page is SessionPage existing && existing.Session.Address == address && existing.Session.Mode == mode
                && existing.Session.State != ClientState.Closed)
            {
                _title.Select(t);
                return;
            }

        var session = new ClientSession(address, mode, Discovery);
        var page = new SessionPage(this, session) { Visible = false };
        var tab = new TabItem { Title = DeskId.Format(address), Page = page, Glyph = mode == "files" ? Glyph.Folder : Glyph.Monitor };
        page.Tab = tab;
        _content.Controls.Add(page);
        _title.Add(tab);
        _addr.Text = "";
        _home.AddressText = "";
        session.Start();
    }

    public void CloseSession(SessionPage page)
    {
        page.Shutdown();
        _title.Remove(page.Tab);
        _content.Controls.Remove(page);
        page.Dispose();
    }

    void OnTabSelected(TabItem t)
    {
        foreach (Control c in _content.Controls) c.Visible = c == t.Page;
        t.Page.BringToFront();
        if (t == _homeTab) { _home.RefreshDesk(); _home.RefreshSessions(); }
    }

    public void TitleBarInvalidate() => _title.Invalidate();

    public void SessionsChanged()
    {
        UpdateAutoComplete();
        if (_title.Selected == _homeTab) _home.RefreshSessions();
    }

    void UpdateAutoComplete()
    {
        var src = new AutoCompleteStringCollection();
        foreach (var r in Settings.Current.Recent)
        {
            src.Add(r.Id);
            if (!string.IsNullOrEmpty(r.Alias)) src.Add(r.Alias);
        }
        _addr.Box.AutoCompleteCustomSource = src;
    }

    ContextMenuStrip BuildRecentMenu(bool favouritesOnly)
    {
        var m = Ctl.Menu();
        var list = Settings.Current.Recent.Where(r => !favouritesOnly || r.Favorite).Take(15).ToList();
        foreach (var r in list)
        {
            var label = r.Title + (string.IsNullOrEmpty(r.Name) || r.Title == r.Name ? "" : "   (" + r.Name + ")");
            var item = Ctl.Item(label, () => Connect(r.Id, "control"), r.Favorite ? Glyph.StarFill : Glyph.Monitor);
            item.Image = Ctl.GlyphImage(Discovery?.IsOnline(r.Id) == true ? Glyph.Monitor : Glyph.Monitor,
                Discovery?.IsOnline(r.Id) == true ? Theme.Online : Theme.SubText);
            m.Items.Add(item);
        }
        if (list.Count == 0)
            m.Items.Add(new ToolStripMenuItem(favouritesOnly ? "No favourites yet – click the ☆ on a session." : "No recent sessions.") { Enabled = false });
        return m;
    }

    void ShowMainMenu(Control anchor, Point at)
    {
        var m = Ctl.Menu();
        m.Items.Add(Ctl.Item("Settings", () => ShowSettings(null), Glyph.Settings));
        m.Items.Add(Ctl.Item(Settings.Current.HasPassword ? "Change unattended access password…" : "Set password for unattended access…", () => SetPasswordDialog(), Glyph.Lock));
        m.Items.Add(Ctl.Item("Copy my address", () => { Clipboard.SetText(Settings.Current.DeskId); Toast("Address copied."); }, Glyph.Copy));
        if (!Installer.IsInstalled) m.Items.Add(Ctl.Item("Install RemoteCraft…", Install, Glyph.Download));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Ctl.Item("About RemoteCraft", () => ShowSettings("About"), Glyph.Info));
        m.Items.Add(Ctl.Item("Exit", ExitApp, Glyph.Power));
        m.Show(anchor, at);
    }

    public void ShowSettings(string page)
    {
        using var f = new SettingsForm(this, page);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _home.RefreshDesk();
            UpdateStatus();
        }
    }

    public void SetPasswordDialog(IWin32Window owner = null)
    {
        using var f = new PasswordForm(Settings.Current.HasPassword);
        if (f.ShowDialog(owner ?? this) != DialogResult.OK) return;
        Settings.Current.SetPassword(f.ClearPassword ? null : f.Password);
        _home.RefreshDesk();
        Toast(f.ClearPassword ? "Unattended access is disabled." : "Password set: this desk can now be reached without confirmation.");
    }

    public void ShowDeskInfo()
    {
        var s = Settings.Current;
        var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address + (Host?.Port is int p && p != 7171 ? ":" + p : ""));
        MessageBox.Show(this,
            $"Desk ID: {DeskId.Format(s.DeskId)}\n\n" +
            "On the same network, others can connect with this id (found automatically by discovery) or directly by IP address:\n  " +
            string.Join("\n  ", ips) + "\n\n" +
            (string.IsNullOrWhiteSpace(s.RelayServer)
                ? "From outside the network: set a relay server in Settings → Connection on both sides."
                : "Relay: " + RelayStatus),
            "This Desk", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Install()
    {
        try
        {
            var exe = Installer.Install();
            Settings.Current.InstallCardHidden = true;
            Settings.Current.Save();
            _home.RefreshDesk();
            Toast("RemoteCraft is installed and starts with Windows. Shortcuts were added to the Start menu and desktop.");
            if (!Installer.RunningFromInstall &&
                MessageBox.Show(this, "RemoteCraft was installed to\n" + exe + "\n\nSwitch to the installed copy now?", "Install RemoteCraft",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                ExitApp();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Arguments = AppPaths.Profile != "" ? "--profile " + AppPaths.Profile : "",
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Installation failed: " + ex.Message, "RemoteCraft", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- window chrome ----------

    public void Toast(string text, Action onClick = null)
    {
        _toastAction = onClick;
        _toast.Text = text;
        var size = TextRenderer.MeasureText(text, _toast.Font);
        _toast.Size = new Size(Math.Min(ClientSize.Width - Theme.S(40), size.Width + Theme.S(40)), Theme.S(40));
        _toast.Location = new Point((ClientSize.Width - _toast.Width) / 2, ClientSize.Height - _toast.Height - Theme.S(30));
        _toast.Cursor = onClick != null ? Cursors.Hand : Cursors.Default;
        _toast.Visible = true;
        _toast.BringToFront();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void Flash() => FlashWindow(Handle, true);

    /// <summary>Maximizes to the monitor's working area (a borderless form would otherwise cover the taskbar).</summary>
    public void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; return; }
        var scr = Screen.FromControl(this);
        var wa = scr.WorkingArea;
        MaximizedBounds = new Rectangle(wa.X - scr.Bounds.X, wa.Y - scr.Bounds.Y, wa.Width, wa.Height);
        WindowState = FormWindowState.Maximized;
    }

    public void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    public void SetFullscreen(bool on)
    {
        if (on == IsFullscreen) return;
        IsFullscreen = on;
        if (on)
        {
            _prevState = WindowState;
            _prevBounds = Bounds;
            _title.Visible = _addressBar.Visible = false;
            Padding = Padding.Empty;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            _title.Visible = _addressBar.Visible = true;
            Padding = new Padding(Theme.S(4));
            Bounds = _prevBounds;
            WindowState = _prevState;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x20000 | 0x80000;  // WS_MINIMIZEBOX | WS_SYSMENU: taskbar minimize/restore
            cp.ClassStyle |= 0x20000;       // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsFullscreen) Padding = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(Theme.S(4));
        _title?.Invalidate();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (WindowState == FormWindowState.Normal && !IsFullscreen)
        {
            using var pen = new Pen(Theme.Red);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCHITTEST && WindowState == FormWindowState.Normal && !IsFullscreen)
        {
            base.WndProc(ref m);
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            const int g = 6;
            bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
            int hit = t && l ? Native.HTTOPLEFT : t && r ? Native.HTTOPRIGHT : b && l ? Native.HTBOTTOMLEFT : b && r ? Native.HTBOTTOMRIGHT
                : l ? Native.HTLEFT : r ? Native.HTRIGHT : t ? Native.HTTOP : b ? Native.HTBOTTOM : 0;
            if (hit != 0) m.Result = hit;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            if (IsFullscreen) SetFullscreen(false);
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "RemoteCraft is still running",
                    "This desk stays reachable. Right-click the icon in the notification area to exit.", ToolTipIcon.Info);
            }
            return;
        }
        Shutdown();
        base.OnFormClosing(e);
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void Shutdown()
    {
        foreach (var t in _title.Tabs.ToList())
            if (t.Page is SessionPage sp) sp.Shutdown();
        _relay?.Dispose();
        Discovery?.Dispose();
        Host?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
