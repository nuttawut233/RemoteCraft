using RemoteCraft.Host;

namespace RemoteCraft.UI;

/// <summary>
/// Host-side window for an incoming session: accept / dismiss with permissions, then the
/// running-session panel with live permission toggles, chat and disconnect.
/// </summary>
public sealed class HostSessionForm : Form
{
    readonly HostSession _s;
    readonly Label _who, _status;
    readonly Panel _perms, _footer;
    readonly FlatButton _accept, _dismiss, _disconnect;
    readonly Permissions _p;
    readonly System.Windows.Forms.Timer _timer;
    ChatPanel _chat;

    public HostSessionForm(HostSession session)
    {
        _s = session;
        _p = (session.Accepted ? session.Perms : Settings.Current.RequestPerms).Clone();
        Text = "RemoteCraft – " + (session.Client.Name ?? "Incoming session");
        Icon = Logo.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Color.White;
        Font = Theme.Ui(10);
        ClientSize = new Size(Theme.S(440), Theme.S(300));
        StartPosition = FormStartPosition.Manual;

        var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(104), BackColor = Color.White };
        var avatar = new Panel { Bounds = new Rectangle(Theme.S(20), Theme.S(20), Theme.S(64), Theme.S(64)) };
        avatar.Paint += (_, e) => Theme.DrawAvatar(e.Graphics, new Rectangle(0, 0, Theme.S(63), Theme.S(63)), session.Client.Name);
        _who = Ctl.Label(session.Client.Name ?? "Unknown", 14, Theme.Text, FontStyle.Bold, autoSize: false);
        _who.SetBounds(Theme.S(100), Theme.S(16), Theme.S(320), Theme.S(30));
        var id = Ctl.Label($"{DeskId.Format(session.Client.Id)}  ·  {session.Client.User}  ·  {session.Via}", 9.5f, Theme.SubText, autoSize: false);
        id.SetBounds(Theme.S(100), Theme.S(46), Theme.S(330), Theme.S(22));
        _status = Ctl.Label("", 10, Theme.Heading, autoSize: false);
        _status.SetBounds(Theme.S(100), Theme.S(70), Theme.S(330), Theme.S(24));
        header.Controls.AddRange(new Control[] { avatar, _who, id, _status });
        header.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Line), 0, header.Height - 1, header.Width, header.Height - 1);

        _perms = new Panel { Dock = DockStyle.Top, Height = Theme.S(92), BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA) };
        var pl = Ctl.Label("PERMISSIONS", 9, Theme.SubText);
        pl.Location = new Point(Theme.S(20), Theme.S(10));
        _perms.Controls.Add(pl);
        int x = Theme.S(20);
        AddToggle(Glyph.Mouse, "Keyboard and mouse", () => _p.Input, v => _p.Input = v, ref x);
        AddToggle(Glyph.Clipboard, "Clipboard", () => _p.Clipboard, v => _p.Clipboard = v, ref x);
        AddToggle(Glyph.Folder, "File manager", () => _p.Files, v => _p.Files = v, ref x);
        AddToggle(Glyph.Lock, "Lock this desk", () => _p.Lock, v => _p.Lock = v, ref x);

        _footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(64), BackColor = Color.White };
        _accept = FlatButton.Green("Accept");
        _accept.Bounds = new Rectangle(Theme.S(250), Theme.S(12), Theme.S(170), Theme.S(40));
        _accept.Click += (_, _) => _s.Accept(_p);
        _dismiss = FlatButton.GrayBtn("Dismiss");
        _dismiss.Bounds = new Rectangle(Theme.S(66), Theme.S(12), Theme.S(170), Theme.S(40));
        _dismiss.Click += (_, _) => { _s.Reject("The remote side dismissed the request."); Close(); };
        _disconnect = FlatButton.RedBtn("Disconnect");
        _disconnect.Bounds = new Rectangle(Theme.S(250), Theme.S(12), Theme.S(170), Theme.S(40));
        _disconnect.Click += (_, _) => { _s.Close("The session was ended by the remote side."); Close(); };
        _footer.Controls.AddRange(new Control[] { _accept, _dismiss, _disconnect });

        Controls.Add(_perms);
        Controls.Add(header);
        Controls.Add(_footer);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();

        _s.StateChanged += OnState;
        _s.ChatReceived += OnChat;
        OnState();
    }

    void AddToggle(string glyph, string tip, Func<bool> get, Action<bool> set, ref int x)
    {
        var b = new IconButton(glyph, tip)
        {
            Bounds = new Rectangle(x, Theme.S(36), Theme.S(48), Theme.S(44)),
            GlyphSize = 15,
            Checked = get(),
            CheckedColor = Color.FromArgb(0xD5, 0xEE, 0xD8),
        };
        b.Click += (_, _) =>
        {
            set(!get());
            b.Checked = get();
            b.GlyphColor = b.Checked ? Theme.Text : Color.FromArgb(0xB0, 0xB0, 0xB0);
            b.Invalidate();
            if (_s.Accepted) _s.SetPermissions(_p);
        };
        b.GlyphColor = b.Checked ? Theme.Text : Color.FromArgb(0xB0, 0xB0, 0xB0);
        _perms.Controls.Add(b);
        x += Theme.S(56);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int offset = 0;
        foreach (Form f in Application.OpenForms)
            if (f is HostSessionForm && f != this) offset += 30;
        Location = new Point(wa.Right - Width - 16 - offset, wa.Bottom - Height - 16 - offset);
        if (!_s.Accepted) System.Media.SystemSounds.Asterisk.Play();
    }

    void OnChat(string text)
    {
        EnsureChat();
        _chat.AddRemote(text);
        if (!ContainsFocus) Activate();
    }

    void EnsureChat()
    {
        if (_chat != null) return;
        _chat = new ChatPanel(_s.Client.Name ?? "Remote", t => _s.SendChat(t), header: true) { Dock = DockStyle.Fill };
        Controls.Add(_chat);
        _chat.BringToFront();
        ClientSize = new Size(ClientSize.Width, Theme.S(520));
        Top = Math.Max(0, Top - Theme.S(220));
    }

    void OnState()
    {
        if (_s.Closed)
        {
            _timer.Stop();
            Close();
            return;
        }
        _accept.Visible = _dismiss.Visible = !_s.Accepted;
        _disconnect.Visible = _s.Accepted;
        if (_s.Accepted)
        {
            _disconnect.Left = (ClientSize.Width - _disconnect.Width) / 2;
            EnsureChat();
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (!_s.Accepted)
            _status.Text = _s.IsFileOnly ? "wants to access your files" : "wants to view and control your desk";
        else
        {
            var t = DateTime.Now - _s.Started;
            _status.Text = (_s.IsFileOnly ? "is accessing your files  " : "is connected to your desk  ") + $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_s.Closed && e.CloseReason == CloseReason.UserClosing)
        {
            if (_s.Accepted)
            {
                if (MessageBox.Show(this, "End the session with " + _s.Client.Name + "?", "RemoteCraft", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _s.Close("The session was ended by the remote side.");
            }
            else _s.Reject("The remote side dismissed the request.");
        }
        _s.StateChanged -= OnState;
        _s.ChatReceived -= OnChat;
        _timer.Stop();
        base.OnFormClosing(e);
    }
}
