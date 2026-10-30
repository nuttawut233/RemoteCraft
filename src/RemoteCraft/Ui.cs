namespace RemoteCraft;

/// <summary>Marshals work from network threads onto the WinForms UI thread.</summary>
public static class Ui
{
    static SynchronizationContext _ctx;

    public static void Init() => _ctx = SynchronizationContext.Current;

    public static void Post(Action action) => _ctx?.Post(_ =>
    {
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }, null);
}

/// <summary>
/// Watches the local clipboard (text) for both sides of a session, and applies remote text
/// without echoing it straight back.
/// </summary>
public sealed class ClipboardHub : NativeWindow
{
    static ClipboardHub _instance;
    static string _lastSet;

    public static event Action<string> TextChanged;

    public static void Init()
    {
        _instance = new ClipboardHub();
        _instance.CreateHandle(new CreateParams());
        Native.AddClipboardFormatListener(_instance.Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (text == _lastSet) _lastSet = null; // our own write: don't bounce it back
                    else if (!string.IsNullOrEmpty(text)) TextChanged?.Invoke(text);
                }
            }
            catch { }
        }
        base.WndProc(ref m);
    }

    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Ui.Post(() =>
        {
            _lastSet = text;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch { Thread.Sleep(40); }
            }
        });
    }
}
