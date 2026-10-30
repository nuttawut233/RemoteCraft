namespace RemoteCraft.UI;

/// <summary>Session chat used on both the viewer and the host side.</summary>
public sealed class ChatPanel : Panel
{
    readonly RichTextBox _log;
    readonly TextBox _input;
    readonly Action<string> _send;
    readonly string _peerName;

    public ChatPanel(string peerName, Action<string> send, bool header = true)
    {
        _peerName = peerName;
        _send = send;
        BackColor = Color.White;
        Padding = new Padding(Theme.S(10));

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = Theme.Ui(10),
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(40), Padding = new Padding(0, Theme.S(6), 0, 0) };
        _input = new TextBox { Dock = DockStyle.Fill, Font = Theme.Ui(10.5f), PlaceholderText = "Type a message…" };
        var sendBtn = new IconButton(Glyph.Send, "Send") { Dock = DockStyle.Right, Width = Theme.S(36) };
        sendBtn.Click += (_, _) => Send();
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); } };
        bottom.Controls.Add(_input);
        bottom.Controls.Add(sendBtn);

        Controls.Add(_log);
        Controls.Add(bottom);
        if (header)
        {
            var title = Ctl.Label("Chat", 13, Theme.Heading);
            title.Dock = DockStyle.Top;
            title.AutoSize = false;
            title.Height = Theme.S(34);
            Controls.Add(title);
        }
        Paint += (_, e) => e.Graphics.DrawLine(new Pen(Theme.Line), 0, 0, 0, Height);
    }

    public new void Focus() => _input.Focus();

    void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        _send(text);
        Add("You", text, true);
    }

    public void AddRemote(string text) => Add(_peerName, text, false);

    public void Add(string who, string text, bool mine)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionFont = Theme.Ui(8.5f, FontStyle.Bold);
        _log.SelectionColor = mine ? Theme.Link : Theme.Heading;
        _log.AppendText($"{who}  {DateTime.Now:HH:mm}\n");
        _log.SelectionFont = Theme.Ui(10);
        _log.SelectionColor = Theme.Text;
        _log.AppendText(text + "\n\n");
        _log.ScrollToCaret();
    }
}
