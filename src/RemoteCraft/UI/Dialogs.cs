namespace RemoteCraft.UI;

/// <summary>Small modal dialogs styled like the rest of the app.</summary>
public abstract class DialogBase : Form
{
    protected readonly Panel Body;
    protected readonly FlatButton Ok, Cancel;

    protected DialogBase(string title, int width, int height, string okText = "OK")
    {
        width = Theme.S(width);
        height = Theme.S(height);
        Text = title;
        Icon = Logo.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = Theme.Ui(10);
        ClientSize = new Size(width, height);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(62), BackColor = Theme.Bg };
        Ok = FlatButton.Green(okText);
        Ok.Size = new Size(Theme.S(130), Theme.S(38));
        Cancel = FlatButton.GrayBtn("Cancel");
        Cancel.Size = new Size(Theme.S(130), Theme.S(38));
        Ok.Location = new Point(width - Theme.S(150), Theme.S(12));
        Cancel.Location = new Point(width - Theme.S(290), Theme.S(12));
        Ok.Click += (_, _) => { if (ValidateInput()) { DialogResult = DialogResult.OK; Close(); } };
        Cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.AddRange(new Control[] { Ok, Cancel });

        Body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(24), Theme.S(18), Theme.S(24), Theme.S(10)), BackColor = Color.White };
        Controls.Add(Body);
        Controls.Add(footer);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter && ActiveControl is not Button) { e.SuppressKeyPress = true; if (ValidateInput()) { DialogResult = DialogResult.OK; Close(); } }
        };
    }

    protected virtual bool ValidateInput() => true;
}

public sealed class Prompt : DialogBase
{
    readonly BoxedTextBox _box;

    Prompt(string title, string label, string value) : base(title, 440, 190)
    {
        var l = Ctl.Label(label, 10.5f, Theme.Text, autoSize: false);
        l.SetBounds(Theme.S(24), Theme.S(18), Theme.S(390), Theme.S(40));
        _box = new BoxedTextBox("", 11) { Bounds = new Rectangle(Theme.S(24), Theme.S(62), Theme.S(390), Theme.S(38)), Text = value };
        Body.Controls.AddRange(new Control[] { l, _box });
        Shown += (_, _) => { _box.Box.Focus(); _box.Box.SelectAll(); };
    }

    public static string Show(IWin32Window owner, string title, string label, string value)
    {
        using var p = new Prompt(title, label, value);
        return p.ShowDialog(owner) == DialogResult.OK ? p._box.Text : null;
    }
}

public sealed class PasswordForm : DialogBase
{
    readonly BoxedTextBox _pw1, _pw2;
    readonly Label _error;
    readonly CheckBox _clear;

    public string Password { get; private set; }
    public bool ClearPassword => _clear.Checked;

    public PasswordForm(bool hasPassword) : base("Unattended Access", 480, hasPassword ? 380 : 350, "Apply")
    {
        var title = Ctl.Label("Set password for unattended access", 14, Theme.Heading);
        title.Location = new Point(Theme.S(24), Theme.S(16));
        var text = Ctl.Label("With a password, this desk can be reached without anyone here accepting the request. " +
                             "Use a long password: anyone who knows it can control this computer.", 10, Theme.SubText, autoSize: false);
        text.SetBounds(Theme.S(24), Theme.S(54), Theme.S(430), Theme.S(60));
        _pw1 = new BoxedTextBox("New password", 11) { Bounds = new Rectangle(Theme.S(24), Theme.S(120), Theme.S(430), Theme.S(38)) };
        _pw2 = new BoxedTextBox("Repeat password", 11) { Bounds = new Rectangle(Theme.S(24), Theme.S(168), Theme.S(430), Theme.S(38)) };
        _pw1.Box.UseSystemPasswordChar = _pw2.Box.UseSystemPasswordChar = true;
        _error = Ctl.Label("", 9.5f, Theme.Red, autoSize: false);
        _error.SetBounds(Theme.S(24), Theme.S(212), Theme.S(430), Theme.S(24));
        _clear = new CheckBox { Text = "Disable unattended access (remove the password)", Font = Theme.Ui(10), Bounds = new Rectangle(Theme.S(24), Theme.S(240), Theme.S(430), Theme.S(26)), Visible = hasPassword };
        _clear.CheckedChanged += (_, _) => _pw1.Enabled = _pw2.Enabled = !_clear.Checked;
        Body.Controls.AddRange(new Control[] { title, text, _pw1, _pw2, _error, _clear });
        Shown += (_, _) => _pw1.Box.Focus();
    }

    protected override bool ValidateInput()
    {
        if (_clear.Checked) return true;
        if (_pw1.Text.Length < 8) { _error.Text = "The password must be at least 8 characters."; return false; }
        if (_pw1.Text != _pw2.Text) { _error.Text = "The passwords don't match."; return false; }
        Password = _pw1.Text;
        return true;
    }
}

/// <summary>Settings: General, Security, Display, Connection, About.</summary>
public sealed class SettingsForm : DialogBase
{
    readonly ListBox _nav;
    readonly Dictionary<string, Panel> _pages = new();
    readonly MainForm _main;

    // General
    TextBox _name;
    CheckBox _tray, _autostart;
    // Security
    CheckBox _allowRequests;
    CheckBox[] _reqPerms, _unPerms;
    Label _pwState;
    // Display
    ComboBox _quality;
    CheckBox _fit;
    // Connection
    NumericUpDown _port;
    CheckBox _discovery;
    TextBox _relay;

    public SettingsForm(MainForm main, string page) : base("Settings", 760, 520, "Save")
    {
        _main = main;
        _nav = new ListBox
        {
            Dock = DockStyle.Left,
            Width = Theme.S(170),
            BorderStyle = BorderStyle.None,
            Font = Theme.Ui(11),
            ItemHeight = Theme.S(36),
            DrawMode = DrawMode.OwnerDrawFixed,
            BackColor = Theme.Bg,
        };
        _nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var b = new SolidBrush(sel ? Color.White : Theme.Bg);
            e.Graphics.FillRectangle(b, e.Bounds);
            if (sel) { using var r = new SolidBrush(Theme.Red); e.Graphics.FillRectangle(r, e.Bounds.X, e.Bounds.Y, 4, e.Bounds.Height); }
            TextRenderer.DrawText(e.Graphics, (string)_nav.Items[e.Index], Theme.Ui(10.5f, sel ? FontStyle.Bold : FontStyle.Regular),
                new Rectangle(e.Bounds.X + Theme.S(18), e.Bounds.Y, e.Bounds.Width - Theme.S(18), e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter);
        };
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(24), Theme.S(10), Theme.S(20), 0) };
        Body.Padding = new Padding(0);
        Body.Controls.Add(host);
        Body.Controls.Add(_nav);

        BuildGeneral();
        BuildSecurity();
        BuildDisplay();
        BuildConnection();
        BuildAbout();
        foreach (var kv in _pages)
        {
            _nav.Items.Add(kv.Key);
            kv.Value.Dock = DockStyle.Fill;
            kv.Value.Visible = false;
            host.Controls.Add(kv.Value);
        }
        _nav.SelectedIndexChanged += (_, _) =>
        {
            foreach (var p in _pages.Values) p.Visible = false;
            if (_nav.SelectedItem is string k) _pages[k].Visible = true;
        };
        _nav.SelectedItem = _pages.ContainsKey(page ?? "") ? page : "General";
    }

    Panel NewPage(string name, string title)
    {
        var p = new Panel { AutoScroll = true };
        var t = Ctl.Label(title, 15, Theme.Heading);
        t.Location = new Point(0, Theme.S(4));
        p.Controls.Add(t);
        _pages[name] = p;
        return p;
    }

    static Label Caption(Panel p, string text, int y)
    {
        var l = Ctl.Label(text, 9, Theme.SubText);
        l.Location = new Point(0, Theme.S(y));
        p.Controls.Add(l);
        return l;
    }

    static CheckBox Check(Panel p, string text, bool value, int y)
    {
        var c = new CheckBox { Text = text, Checked = value, Font = Theme.Ui(10), AutoSize = true, Location = new Point(0, Theme.S(y)) };
        p.Controls.Add(c);
        return c;
    }

    void BuildGeneral()
    {
        var s = Settings.Current;
        var p = NewPage("General", "General");
        Caption(p, "DISPLAY NAME (shown to the people you connect to)", 52);
        _name = new TextBox { Text = s.DisplayName, Font = Theme.Ui(11), Bounds = new Rectangle(0, Theme.S(74), Theme.S(360), Theme.S(30)) };
        p.Controls.Add(_name);
        Caption(p, "THIS DESK", 124);
        var id = Ctl.Label("Address:  " + DeskId.Format(s.DeskId) + (AppPaths.Profile != "" ? $"    (profile \"{AppPaths.Profile}\")" : ""), 10.5f, Theme.Text);
        id.Location = new Point(0, Theme.S(146));
        p.Controls.Add(id);
        _tray = Check(p, "Keep running in the notification area when the window is closed", s.CloseToTray, 190);
        _autostart = Check(p, "Start RemoteCraft with Windows (in the notification area)", Installer.AutoStart, 220);
    }

    void BuildSecurity()
    {
        var s = Settings.Current;
        var p = NewPage("Security", "Security");
        Caption(p, "INTERACTIVE ACCESS", 52);
        _allowRequests = Check(p, "Allow connection requests (someone here must accept them)", s.AllowRequests, 74);
        Caption(p, "DEFAULT PERMISSIONS FOR ACCEPTED REQUESTS", 112);
        _reqPerms = PermChecks(p, s.RequestPerms, 134);
        Caption(p, "UNATTENDED ACCESS", 210);
        _pwState = Ctl.Label("", 10, Theme.Text);
        _pwState.Location = new Point(0, Theme.S(232));
        p.Controls.Add(_pwState);
        var setPw = Ctl.Link("Set password…", Theme.Link, 10);
        setPw.SetBounds(0, Theme.S(256), Theme.S(200), Theme.S(24));
        setPw.LinkClicked += (_, _) => { _main.SetPasswordDialog(this); UpdatePwState(); };
        p.Controls.Add(setPw);
        Caption(p, "PERMISSIONS FOR UNATTENDED ACCESS", 290);
        _unPerms = PermChecks(p, s.UnattendedPerms, 312);
        UpdatePwState();
    }

    void UpdatePwState() => _pwState.Text = Settings.Current.HasPassword
        ? "Enabled: this desk can be reached with its password."
        : "Disabled: no password is set.";

    static CheckBox[] PermChecks(Panel p, Permissions perms, int y) => new[]
    {
        Check(p, "Keyboard and mouse", perms.Input, y),
        Check(p, "Clipboard", perms.Clipboard, y + 26),
        Check(p, "File manager", perms.Files, y + 52),
        AddRight(Check(p, "Lock this desk", perms.Lock, y), 240),
    };

    static CheckBox AddRight(CheckBox c, int x) { c.Left = Theme.S(x); return c; }

    static void ReadPerms(CheckBox[] c, Permissions p)
    {
        p.Input = c[0].Checked;
        p.Clipboard = c[1].Checked;
        p.Files = c[2].Checked;
        p.Lock = c[3].Checked;
    }

    void BuildDisplay()
    {
        var s = Settings.Current;
        var p = NewPage("Display", "Display");
        Caption(p, "QUALITY", 52);
        _quality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui(10.5f), Bounds = new Rectangle(0, Theme.S(74), Theme.S(300), Theme.S(30)) };
        _quality.Items.AddRange(new object[] { "Best quality", "Balanced", "Optimise reaction time" });
        _quality.SelectedIndex = s.Quality switch { "Best" => 0, "Speed" => 2, _ => 1 };
        p.Controls.Add(_quality);
        _fit = Check(p, "Fit the remote screen to the window (otherwise 1:1, panning with the mouse)", s.FitToWindow, 122);
    }

    void BuildConnection()
    {
        var s = Settings.Current;
        var p = NewPage("Connection", "Connection");
        Caption(p, "DIRECT CONNECTIONS (TCP port this desk listens on)", 52);
        _port = new NumericUpDown { Minimum = 1024, Maximum = 65000, Value = s.Port, Font = Theme.Ui(10.5f), Bounds = new Rectangle(0, Theme.S(74), Theme.S(120), Theme.S(30)) };
        p.Controls.Add(_port);
        var cur = Ctl.Label(_main.Host?.Port > 0 ? $"Currently listening on port {_main.Host.Port}." : "Not listening.", 9.5f, Theme.SubText);
        cur.Location = new Point(Theme.S(132), Theme.S(78));
        p.Controls.Add(cur);
        _discovery = Check(p, "Discovery: announce this desk and find others on the local network", s.DiscoveryEnabled, 118);
        Caption(p, "RELAY SERVER (to reach desks by id across the internet), e.g. relay.example.com:7180", 162);
        _relay = new TextBox { Text = s.RelayServer, Font = Theme.Ui(10.5f), Bounds = new Rectangle(0, Theme.S(184), Theme.S(360), Theme.S(30)), PlaceholderText = "host[:port]  (empty = LAN only)" };
        p.Controls.Add(_relay);
        var rs = Ctl.Label(_main.RelayStatus ?? "", 9.5f, Theme.SubText, autoSize: false);
        rs.SetBounds(0, Theme.S(218), Theme.S(520), Theme.S(40));
        p.Controls.Add(rs);
        var note = Ctl.Label("Run RemoteCraft.Relay.exe on a server that both sides can reach (open TCP port 7180).\n" +
                             "Sessions stay end-to-end encrypted; the relay only forwards bytes.", 9.5f, Theme.SubText, autoSize: false);
        note.SetBounds(0, Theme.S(262), Theme.S(540), Theme.S(50));
        p.Controls.Add(note);
    }

    void BuildAbout()
    {
        var p = NewPage("About", "About RemoteCraft");
        var logo = new Panel { Bounds = new Rectangle(0, Theme.S(56), Theme.S(72), Theme.S(72)) };
        logo.Paint += (_, e) => Logo.Draw(e.Graphics, new RectangleF(0, 0, Theme.S(72), Theme.S(72)));
        p.Controls.Add(logo);
        var name = Ctl.Label("RemoteCraft " + Program.Version, 14, Theme.Text, FontStyle.Bold);
        name.Location = new Point(Theme.S(90), Theme.S(62));
        p.Controls.Add(name);
        var text = Ctl.Label(
            "Remote desktop for Windows: screen sharing and remote control, file transfer,\n" +
            "chat, clipboard sync, unattended access, LAN discovery and an optional relay.\n\n" +
            "Encryption: ECDH P-256 key agreement, AES-256-GCM.\n" +
            "Settings: " + AppPaths.Root, 9.5f, Theme.SubText, autoSize: false);
        text.SetBounds(Theme.S(90), Theme.S(96), Theme.S(450), Theme.S(120));
        p.Controls.Add(text);
    }

    protected override bool ValidateInput()
    {
        var s = Settings.Current;
        var name = _name.Text.Trim();
        if (name.Length == 0) { _nav.SelectedItem = "General"; _name.Focus(); return false; }
        bool restartNet = s.Port != (int)_port.Value || s.RelayServer != _relay.Text.Trim() || s.DiscoveryEnabled != _discovery.Checked;
        s.DisplayName = name;
        s.CloseToTray = _tray.Checked;
        s.AllowRequests = _allowRequests.Checked;
        ReadPerms(_reqPerms, s.RequestPerms);
        ReadPerms(_unPerms, s.UnattendedPerms);
        s.Quality = _quality.SelectedIndex switch { 0 => "Best", 2 => "Speed", _ => "Balanced" };
        s.FitToWindow = _fit.Checked;
        s.Port = (int)_port.Value;
        s.DiscoveryEnabled = _discovery.Checked;
        s.RelayServer = _relay.Text.Trim();
        s.Save();
        if (_autostart.Checked != Installer.AutoStart) Installer.AutoStart = _autostart.Checked;
        if (restartNet) _main.RestartNetwork();
        return true;
    }
}
