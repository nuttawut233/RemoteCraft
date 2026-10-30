using System.Drawing.Imaging;
using RemoteCraft.Client;
using RemoteCraft.Host;

namespace RemoteCraft.UI;

/// <summary>A session tab: toolbar, remote screen (or file manager), chat, and the connect / accept overlay.</summary>
public sealed class SessionPage : Panel
{
    readonly MainForm _main;
    readonly Panel _toolbar, _body, _overlay;
    readonly Label _name, _latency, _ovTitle, _ovText;
    readonly Panel _ovCard;
    readonly BoxedTextBox _password;
    readonly CheckBox _remember;
    readonly FlatButton _login, _cancel, _reconnect;
    readonly IconButton _chatBtn, _displayBtn, _actionsBtn, _clipBtn, _filesBtn, _shotBtn, _fullBtn, _closeBtn, _inputBtn;
    ViewerControl _viewer;
    FileManagerControl _files;
    ChatPanel _chat;
    readonly System.Windows.Forms.Timer _timer;
    bool _recorded;

    public ClientSession Session { get; private set; }
    public TabItem Tab { get; set; }

    public SessionPage(MainForm main, ClientSession session)
    {
        _main = main;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;

        // ---------- toolbar ----------
        _toolbar = new Panel { Dock = DockStyle.Top, Height = Theme.S(44), BackColor = Color.White };
        _toolbar.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Line), 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
        _name = new Label { AutoSize = false, Font = Theme.Ui(10.5f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(Theme.S(14), Theme.S(4), Theme.S(200), Theme.S(36)) };
        _chatBtn = new IconButton(Glyph.Chat, "Chat");
        _displayBtn = new IconButton(Glyph.Monitor, "Display") { DropDown = true, Width = Theme.S(50) };
        _actionsBtn = new IconButton(Glyph.Bolt, "Actions") { DropDown = true, Width = Theme.S(50) };
        _inputBtn = new IconButton(Glyph.Mouse, "Control the remote desk (off = view only)") { Checked = true };
        _clipBtn = new IconButton(Glyph.Clipboard, "Synchronise clipboard") { Checked = true };
        _filesBtn = new IconButton(Glyph.Folder, "File transfer");
        _shotBtn = new IconButton(Glyph.Camera, "Take a screenshot");
        _fullBtn = new IconButton(Glyph.FullScreen, "Full screen (F11)");
        _closeBtn = new IconButton(Glyph.Power, "Disconnect") { GlyphColor = Theme.Red };
        _latency = new Label { AutoSize = false, Font = Theme.Ui(9), ForeColor = Theme.SubText, TextAlign = ContentAlignment.MiddleRight, Size = new Size(Theme.S(220), Theme.S(36)) };

        int x = Theme.S(220);
        foreach (var b in new[] { _chatBtn, _displayBtn, _actionsBtn, _inputBtn, _clipBtn, _filesBtn, _shotBtn, _fullBtn })
        {
            b.Location = new Point(x, Theme.S(4));
            x += b.Width + 2;
            _toolbar.Controls.Add(b);
        }
        _toolbar.Controls.AddRange(new Control[] { _name, _latency, _closeBtn });
        _toolbar.Resize += (_, _) =>
        {
            _closeBtn.Location = new Point(_toolbar.Width - Theme.S(52), Theme.S(4));
            _latency.Location = new Point(_toolbar.Width - Theme.S(280), Theme.S(4));
        };

        _chatBtn.Click += (_, _) => ToggleChat();
        _displayBtn.Click += (_, _) => _displayBtn.ShowMenu(BuildDisplayMenu());
        _actionsBtn.Click += (_, _) => _actionsBtn.ShowMenu(BuildActionsMenu());
        _inputBtn.Click += (_, _) =>
        {
            _inputBtn.Checked = !_inputBtn.Checked;
            if (_viewer != null) { _viewer.ViewOnly = !_inputBtn.Checked; _viewer.ReleaseAll(); }
            _inputBtn.Invalidate();
        };
        _clipBtn.Click += (_, _) =>
        {
            _clipBtn.Checked = !_clipBtn.Checked;
            Session.SyncClipboard = _clipBtn.Checked;
            _clipBtn.Invalidate();
        };
        _filesBtn.Click += (_, _) => ToggleFiles();
        _shotBtn.Click += (_, _) => Screenshot();
        _fullBtn.Click += (_, _) => ToggleFullscreen();
        _closeBtn.Click += (_, _) => _main.CloseSession(this);

        _body = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x3C, 0x3C, 0x3C) };

        // ---------- overlay ----------
        _overlay = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _ovCard = new Panel { Size = new Size(Theme.S(560), Theme.S(300)), BackColor = Color.White };
        _ovCard.Paint += (_, e) =>
        {
            using var b = new SolidBrush(Session?.State == ClientState.Closed ? Theme.Red : Theme.Orange);
            e.Graphics.FillRectangle(b, 0, 0, 6, _ovCard.Height);
        };
        _ovTitle = Ctl.Label("", 16, Theme.Heading, autoSize: false);
        _ovTitle.SetBounds(Theme.S(30), Theme.S(22), Theme.S(510), Theme.S(34));
        _ovText = Ctl.Label("", 10.5f, Theme.SubText, autoSize: false);
        _ovText.SetBounds(Theme.S(30), Theme.S(64), Theme.S(510), Theme.S(60));
        _password = new BoxedTextBox("Password for unattended access", 11) { Bounds = new Rectangle(Theme.S(30), Theme.S(128), Theme.S(500), Theme.S(38)) };
        _password.Box.UseSystemPasswordChar = true;
        _password.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Login(); } };
        _remember = new CheckBox { Text = "Log in automatically from now on", Font = Theme.Ui(10), ForeColor = Theme.Text, Bounds = new Rectangle(Theme.S(30), Theme.S(174), Theme.S(300), Theme.S(26)), Checked = true };
        _login = FlatButton.Green("Log in");
        _login.Bounds = new Rectangle(Theme.S(380), Theme.S(230), Theme.S(150), Theme.S(40));
        _login.Click += (_, _) => Login();
        _cancel = FlatButton.GrayBtn("Cancel");
        _cancel.Bounds = new Rectangle(Theme.S(216), Theme.S(230), Theme.S(150), Theme.S(40));
        _cancel.Click += (_, _) => _main.CloseSession(this);
        _reconnect = FlatButton.Green("Reconnect");
        _reconnect.Bounds = new Rectangle(Theme.S(380), Theme.S(230), Theme.S(150), Theme.S(40));
        _reconnect.Click += (_, _) => Reconnect();
        _ovCard.Controls.AddRange(new Control[] { _ovTitle, _ovText, _password, _remember, _login, _cancel, _reconnect });
        _overlay.Controls.Add(_ovCard);
        _overlay.Resize += (_, _) => _ovCard.Location = new Point(Math.Max(10, (_overlay.Width - _ovCard.Width) / 2), Math.Max(10, (_overlay.Height - _ovCard.Height) / 2 - Theme.S(40)));

        Controls.Add(_body);
        Controls.Add(_overlay);
        Controls.Add(_toolbar);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateLatency();
        _timer.Start();

        Attach(session);
    }

    /// <summary>Recent-list key: the desk id when dialled by id, else the host[:port] that was dialled.</summary>
    string RecentKey => DeskId.IsId(Session.Address) && Session.Info != null ? Session.Info.Id : Session.Address;

    public string TabTitle
    {
        get
        {
            var recent = Settings.Current.Recent.FirstOrDefault(r => r.Id == RecentKey);
            var id = Session.Info?.Id ?? Session.Address;
            return !string.IsNullOrEmpty(recent?.Alias) ? recent.Alias : DeskId.Format(id);
        }
    }

    void Attach(ClientSession session)
    {
        Session = session;
        _recorded = false;
        session.StateChanged += OnStateChanged;
        session.ChatReceived += OnChat;
        OnStateChanged();
    }

    void OnChat(string text)
    {
        if (_chat == null || !_chat.Visible) ToggleChat();
        _chat.AddRemote(text);
        if (!_main.ContainsFocus) FlashWindow();
    }

    void FlashWindow() => _main.Flash();

    void OnStateChanged()
    {
        var s = Session;
        bool files = s.Mode == "files";
        string who = DeskId.Format(s.Info?.Id ?? s.Address);
        _name.Text = s.Info != null ? $"{s.Info.Name}  ·  {who}" : who;
        if (Tab != null)
        {
            Tab.Title = (files ? "Files · " : "") + TabTitle;
            Tab.Glyph = files ? Glyph.Folder : Glyph.Monitor;
            Tab.Dot = s.State switch
            {
                ClientState.Connected => Theme.Online,
                ClientState.Closed => Theme.Red,
                _ => Theme.Orange,
            };
            _main.TitleBarInvalidate();
        }

        bool connected = s.State == ClientState.Connected;
        foreach (var b in new[] { _chatBtn, _displayBtn, _actionsBtn, _inputBtn, _clipBtn, _filesBtn, _shotBtn, _fullBtn })
            b.Enabled = connected && (!files || b == _chatBtn);
        _closeBtn.Enabled = s.State != ClientState.Closed;

        if (connected)
        {
            if (!_recorded)
            {
                _recorded = true;
                Settings.Current.Touch(RecentKey, s.Info?.Name);
                _main.SessionsChanged();
                if (files) ShowFiles(true);
                else ShowViewer();
            }
            _overlay.Visible = false;
            _inputBtn.Enabled = connected && s.Perms.Input;
            _filesBtn.Enabled = connected && s.Perms.Files;
            _clipBtn.Enabled = connected && s.Perms.Clipboard;
            if (files) _filesBtn.Checked = true;
            return;
        }

        if (s.State == ClientState.Closed && _viewer != null) SaveThumbnail();
        _overlay.Visible = true;
        _overlay.BringToFront();
        _ovCard.Invalidate();
        bool waiting = s.State == ClientState.Waiting;
        bool closed = s.State == ClientState.Closed;
        _ovTitle.Text = s.State switch
        {
            ClientState.Connecting => "Connecting to " + who + "…",
            ClientState.Waiting => (files ? "Requesting file access on " : "Requesting a session with ") + who,
            _ => "Session ended",
        };
        _ovTitle.ForeColor = closed ? Theme.Red : Theme.Heading;
        var text = s.StatusText;
        if (waiting && s.PasswordAvailable) text += "\nThe remote desk allows unattended access: you can log in with its password instead.";
        if (s.Fingerprint != null && !closed) text += "\nSecurity code: " + s.Fingerprint + (s.Via != null ? "  ·  via " + s.Via : "");
        _ovText.Text = text;

        bool pw = waiting && s.PasswordAvailable;
        _password.Visible = _remember.Visible = _login.Visible = pw;
        _reconnect.Visible = closed;
        _cancel.Text = closed ? "Close" : "Cancel";
        _cancel.Location = new Point(Theme.S(closed || pw ? 216 : 380), Theme.S(230));
        _ovCard.Height = Theme.S(290);
        if (pw && !_password.Box.Focused) _password.Box.Focus();
    }

    void Login()
    {
        if (string.IsNullOrEmpty(_password.Text)) return;
        Session.LoginWithPassword(_password.Text, _remember.Checked);
        _password.Text = "";
    }

    void Reconnect()
    {
        Session.StateChanged -= OnStateChanged;
        Session.ChatReceived -= OnChat;
        DisposeContent();
        var s = new ClientSession(Session.Address, Session.Mode, _main.Discovery);
        Attach(s);
        s.Start();
    }

    void DisposeContent()
    {
        _body.Controls.Clear();
        _viewer?.Dispose(); _viewer = null;
        _files?.Dispose(); _files = null;
        _chat?.Dispose(); _chat = null;
        _filesBtn.Checked = _chatBtn.Checked = false;
    }

    void ShowViewer()
    {
        _viewer = new ViewerControl(Session) { Dock = DockStyle.Fill, Fit = Settings.Current.FitToWindow };
        _body.Controls.Add(_viewer);
        _viewer.BringToFront();
        _viewer.Focus();
    }

    void ToggleFiles() => ShowFiles(!(_files?.Visible ?? false));

    void ShowFiles(bool show)
    {
        if (show && _files == null)
        {
            _files = new FileManagerControl(Session) { Dock = DockStyle.Fill };
            _body.Controls.Add(_files);
        }
        if (_files != null)
        {
            _files.Visible = show;
            if (show) { _files.BringToFront(); _files.EnsureLoaded(); }
        }
        if (_viewer != null) _viewer.Visible = !show;
        _filesBtn.Checked = show;
        _filesBtn.Invalidate();
        if (!show) _viewer?.Focus();
    }

    void ToggleChat()
    {
        if (_chat == null)
        {
            _chat = new ChatPanel(Session.Info?.Name ?? "Remote", t => Session.SendChat(t)) { Dock = DockStyle.Right, Width = Theme.S(300) };
            _body.Controls.Add(_chat);
            _chat.SendToBack(); // dock before the fill control
            _chat.Visible = false;
        }
        _chat.Visible = !_chat.Visible;
        _chatBtn.Checked = _chat.Visible;
        _chatBtn.Invalidate();
        if (_chat.Visible) _chat.Focus(); else _viewer?.Focus();
    }

    ContextMenuStrip BuildDisplayMenu()
    {
        var m = Ctl.Menu();
        var fit = Ctl.Item("Fit to window", () => SetFit(true), Glyph.BackToWindow);
        fit.Checked = _viewer?.Fit ?? true;
        var orig = Ctl.Item("Original size (1:1)", () => SetFit(false), Glyph.FullScreen);
        orig.Checked = !(_viewer?.Fit ?? true);
        m.Items.Add(fit);
        m.Items.Add(orig);
        m.Items.Add(new ToolStripSeparator());
        foreach (var (q, label) in new[] { ("Best", "Best quality"), ("Balanced", "Balanced"), ("Speed", "Optimise reaction time") })
        {
            var item = Ctl.Item(label, () =>
            {
                Settings.Current.Quality = q;
                Settings.Current.Save();
                Session.SendQuality(q);
            });
            item.Checked = Settings.Current.Quality == q;
            m.Items.Add(item);
        }
        var monitors = Session.Auth?.Monitors;
        if (monitors != null && monitors.Count > 1)
        {
            m.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < monitors.Count; i++)
            {
                int idx = i;
                var mi = monitors[i];
                var item = Ctl.Item($"Monitor {i + 1}  ({mi.W}×{mi.H}{(mi.Primary ? ", primary" : "")})", () => Session.SelectMonitor(idx), Glyph.Monitor);
                item.Checked = Session.Auth.Monitor == i;
                m.Items.Add(item);
            }
        }
        return m;
    }

    void SetFit(bool fit)
    {
        Settings.Current.FitToWindow = fit;
        Settings.Current.Save();
        if (_viewer == null) return;
        _viewer.Fit = fit;
        _viewer.Invalidate();
    }

    ContextMenuStrip BuildActionsMenu()
    {
        var m = Ctl.Menu();
        m.Items.Add(Ctl.Item("Send Ctrl+Alt+Del", () => Session.SendAction(RemoteAction.CtrlAltDel), Glyph.Keyboard));
        m.Items.Add(Ctl.Item("Open Task Manager (Ctrl+Shift+Esc)", () => Session.SendAction(RemoteAction.TaskManager), Glyph.Keyboard));
        m.Items.Add(Ctl.Item("Windows key", () => Session.SendAction(RemoteAction.WinKey), Glyph.Keyboard));
        m.Items.Add(Ctl.Item("Alt+Tab", () => Session.SendAction(RemoteAction.AltTab), Glyph.Keyboard));
        m.Items.Add(new ToolStripSeparator());
        var lockItem = Ctl.Item("Lock remote desk", () => Session.SendAction(RemoteAction.Lock), Glyph.Lock);
        lockItem.Enabled = Session.Perms.Lock;
        m.Items.Add(lockItem);
        m.Items.Add(Ctl.Item("Refresh screen", () => Session.SendAction(RemoteAction.RefreshScreen), Glyph.Refresh));
        m.Items.Add(Ctl.Item("Take screenshot", Screenshot, Glyph.Camera));
        return m;
    }

    void Screenshot()
    {
        Bitmap copy;
        lock (Session.FrameLock)
        {
            if (Session.Frame == null) return;
            copy = new Bitmap(Session.Frame);
        }
        using (copy)
        {
            var name = $"RemoteCraft {DeskId.Normalize(Session.RemoteId)} {DateTime.Now:yyyy-MM-dd HHmmss}.png";
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RemoteCraft"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCraft", "Screenshots"),
                     })
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, name);
                    copy.Save(path, ImageFormat.Png);
                    _main.Toast("Screenshot saved: " + path, () => System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""));
                    return;
                }
                catch { }
            }
            _main.Toast("The screenshot could not be saved.");
        }
    }

    void ToggleFullscreen()
    {
        _main.SetFullscreen(!_main.IsFullscreen);
        _fullBtn.Glyph = _main.IsFullscreen ? Glyph.BackToWindow : Glyph.FullScreen;
        _fullBtn.Invalidate();
        _viewer?.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11 && Session.State == ClientState.Connected && Session.Mode == "control")
        {
            ToggleFullscreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void UpdateLatency()
    {
        var s = Session;
        if (s.State != ClientState.Connected) { _latency.Text = ""; return; }
        var parts = new List<string>();
        if (s.LatencyMs >= 0) parts.Add($"{s.LatencyMs} ms");
        if (s.Via != null) parts.Add(s.Via);
        parts.Add("encrypted");
        _latency.Text = string.Join("  ·  ", parts);
        if (!s.Perms.Input && _viewer != null && !_viewer.ViewOnly) { _viewer.ViewOnly = true; _inputBtn.Checked = false; }
    }

    void SaveThumbnail()
    {
        try
        {
            using var thumb = new Bitmap(384, 216);
            lock (Session.FrameLock)
            {
                if (Session.Frame == null) return;
                using var g = Graphics.FromImage(thumb);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var f = Session.Frame;
                float scale = Math.Max(384f / f.Width, 216f / f.Height);
                g.DrawImage(f, (384 - f.Width * scale) / 2, (216 - f.Height * scale) / 2, f.Width * scale, f.Height * scale);
            }
            thumb.Save(AppPaths.ThumbFor(RecentKey), ImageFormat.Jpeg);
            _main.SessionsChanged();
        }
        catch { }
    }

    /// <summary>Ends the session (tab closing or app exit).</summary>
    public void Shutdown()
    {
        _timer.Stop();
        if (Session.State == ClientState.Connected && _viewer != null) SaveThumbnail();
        Session.StateChanged -= OnStateChanged;
        Session.ChatReceived -= OnChat;
        if (Session.State != ClientState.Closed) Session.Disconnect();
        if (_main.IsFullscreen) _main.SetFullscreen(false);
    }
}
