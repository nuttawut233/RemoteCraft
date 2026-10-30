using System.Collections.Concurrent;
using RemoteCraft.Client;
using RemoteCraft.Net;

namespace RemoteCraft.UI;

/// <summary>Two-pane file manager: this desk on the left, the remote desk on the right.</summary>
public sealed class FileManagerControl : Panel
{
    readonly ClientSession _s;
    readonly FilePane _local, _remote;
    readonly ProgressBar _progress;
    readonly Label _status;
    readonly BlockingCollection<Transfer> _queue = new();
    readonly ConcurrentDictionary<int, Transfer> _active = new();
    int _nextId;
    CancellationTokenSource _cts = new();

    sealed class Transfer
    {
        public int Id;
        public bool Upload;
        public string Local, Remote;
        public long Size, Done;
        public FileStream Stream;
        public string Error;
        public readonly ManualResetEventSlim Finished = new();
    }

    public FileManagerControl(ClientSession session)
    {
        _s = session;
        BackColor = Theme.Bg;
        Padding = new Padding(Theme.S(12));

        _local = new FilePane("This Desk", false) { Dock = DockStyle.Fill };
        _remote = new FilePane("Remote Desk", true) { Dock = DockStyle.Fill };
        _local.NavigateRequested += p => _local.SetListing(FileLister.List(p));
        _remote.NavigateRequested += p => { _remote.SetBusy(); _s.RequestFileList(p); };
        _local.TransferRequested += () => QueueUpload();
        _remote.TransferRequested += () => QueueDownload();

        var middle = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        var up = FlatButton.Green("Upload");
        up.Icon = Glyph.Forward;
        up.Size = new Size(Theme.S(116), Theme.S(38));
        up.Click += (_, _) => QueueUpload();
        var down = FlatButton.GrayBtn("Download");
        down.Icon = Glyph.Back;
        down.Size = new Size(Theme.S(116), Theme.S(38));
        down.Click += (_, _) => QueueDownload();
        middle.Controls.AddRange(new Control[] { up, down });
        middle.Resize += (_, _) =>
        {
            up.Location = new Point((middle.Width - up.Width) / 2, middle.Height / 2 - Theme.S(46));
            down.Location = new Point((middle.Width - down.Width) / 2, middle.Height / 2 + Theme.S(8));
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(150)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.Controls.Add(_local, 0, 0);
        grid.Controls.Add(middle, 1, 0);
        grid.Controls.Add(_remote, 2, 0);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(44), BackColor = Theme.Bg };
        _status = Ctl.Label("Ready.", 10, Theme.SubText);
        _status.Location = new Point(Theme.S(2), Theme.S(14));
        _progress = new ProgressBar { Anchor = AnchorStyles.Right | AnchorStyles.Top, Size = new Size(Theme.S(300), Theme.S(16)), Visible = false };
        var cancel = new IconButton(Glyph.Cancel, "Cancel transfers") { Anchor = AnchorStyles.Right | AnchorStyles.Top, Size = new Size(Theme.S(30), Theme.S(30)), Visible = false };
        cancel.Click += (_, _) => CancelAll();
        bottom.Controls.AddRange(new Control[] { _status, _progress, cancel });
        bottom.Resize += (_, _) =>
        {
            _progress.Location = new Point(bottom.Width - Theme.S(345), Theme.S(14));
            cancel.Location = new Point(bottom.Width - Theme.S(36), Theme.S(7));
        };
        _progress.VisibleChanged += (_, _) => cancel.Visible = _progress.Visible;

        Controls.Add(grid);
        Controls.Add(bottom);

        _s.FileListReceived += OnRemoteListing;
        _s.FileChunkReceived += OnChunk;
        _s.FileStatusReceived += OnStatus;

        _local.SetListing(FileLister.List(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        new Thread(Worker) { IsBackground = true, Name = "FileTransfers" }.Start();
    }

    bool _remoteLoaded;

    /// <summary>Loads the remote root the first time the pane becomes visible.</summary>
    public void EnsureLoaded()
    {
        if (_remoteLoaded) return;
        _remoteLoaded = true;
        _remote.SetBusy();
        _s.RequestFileList("");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.FileListReceived -= OnRemoteListing;
            _s.FileChunkReceived -= OnChunk;
            _s.FileStatusReceived -= OnStatus;
            CancelAll();
            _queue.CompleteAdding();
        }
        base.Dispose(disposing);
    }

    void OnRemoteListing(FileListing l)
    {
        _remote.SetListing(l);
        if (l.Error != null) _status.Text = "Remote: " + l.Error;
    }

    void QueueUpload()
    {
        if (string.IsNullOrEmpty(_remote.CurrentPath)) { _status.Text = "Open a folder on the remote desk first."; return; }
        int n = 0;
        foreach (var e in _local.Selected)
        {
            var full = Path.Combine(_local.CurrentPath, e.Name);
            if (e.Dir)
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(_local.CurrentPath, f);
                    Enqueue(new Transfer { Upload = true, Local = f, Remote = Path.Combine(_remote.CurrentPath, rel), Size = new FileInfo(f).Length });
                    n++;
                }
            }
            else
            {
                Enqueue(new Transfer { Upload = true, Local = full, Remote = Path.Combine(_remote.CurrentPath, e.Name), Size = e.Size });
                n++;
            }
        }
        if (n == 0) _status.Text = "Select files or folders on This Desk to upload.";
    }

    void QueueDownload()
    {
        if (string.IsNullOrEmpty(_local.CurrentPath)) { _status.Text = "Open a local folder first."; return; }
        int n = 0;
        foreach (var e in _remote.Selected)
        {
            if (e.Dir) { _status.Text = "Folders can't be downloaded as a whole yet; open the folder and select its files."; continue; }
            Enqueue(new Transfer { Upload = false, Local = Path.Combine(_local.CurrentPath, e.Name), Remote = Path.Combine(_remote.CurrentPath, e.Name), Size = e.Size });
            n++;
        }
        if (n == 0 && _remote.Selected.Count == 0) _status.Text = "Select files on the Remote Desk to download.";
    }

    void Enqueue(Transfer t)
    {
        t.Id = Interlocked.Increment(ref _nextId);
        _queue.Add(t);
        _progress.Visible = true;
    }

    void CancelAll()
    {
        _cts.Cancel();
        while (_queue.TryTake(out _)) { }
        foreach (var t in _active.Values)
        {
            t.Error = "Cancelled";
            t.Finished.Set();
        }
        _cts = new CancellationTokenSource();
    }

    void Worker()
    {
        try
        {
            foreach (var t in _queue.GetConsumingEnumerable())
            {
                _active[t.Id] = t;
                Report(t);
                try
                {
                    if (t.Upload)
                    {
                        _s.Upload(t.Id, t.Local, t.Remote, sent => { t.Done = sent; Report(t); }, _cts.Token);
                        t.Finished.Wait(TimeSpan.FromMinutes(2));
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(t.Local)!);
                        t.Stream = new FileStream(t.Local, FileMode.Create, FileAccess.Write);
                        _s.RequestDownload(t.Id, t.Remote);
                        while (!t.Finished.Wait(500) && _s.State == ClientState.Connected) { }
                        t.Stream?.Dispose();
                        if (t.Error != null) try { File.Delete(t.Local); } catch { }
                    }
                }
                catch (OperationCanceledException) { t.Error = "Cancelled"; }
                catch (Exception ex) { t.Error = ex.Message; }
                _active.TryRemove(t.Id, out _);
                var done = t;
                Ui.Post(() =>
                {
                    _status.Text = done.Error == null
                        ? $"{(done.Upload ? "Uploaded" : "Downloaded")} {Path.GetFileName(done.Local)}"
                        : $"{Path.GetFileName(done.Local)}: {done.Error}";
                    if (_queue.Count == 0)
                    {
                        _progress.Visible = false;
                        if (done.Upload) _remote.Reload(); else _local.SetListing(FileLister.List(_local.CurrentPath));
                    }
                });
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void Report(Transfer t)
    {
        int pct = t.Size > 0 ? (int)Math.Min(100, t.Done * 100 / t.Size) : 0;
        Ui.Post(() =>
        {
            _progress.Value = pct;
            _status.Text = $"{(t.Upload ? "Uploading" : "Downloading")} {Path.GetFileName(t.Local)}  ({FormatSize(t.Done)} of {FormatSize(t.Size)})" +
                           (_queue.Count > 0 ? $"  ·  {_queue.Count} more queued" : "");
        });
    }

    // Network thread.
    void OnChunk(int id, byte[] data, bool final)
    {
        if (!_active.TryGetValue(id, out var t) || t.Stream == null) return;
        try
        {
            t.Stream.Write(data);
            t.Done += data.Length;
            Report(t);
        }
        catch (Exception ex) { t.Error = ex.Message; final = true; }
        if (final) t.Finished.Set();
    }

    void OnStatus(int id, string error, bool done)
    {
        if (id == -1)
        {
            _status.Text = error;
            foreach (var a in _active.Values) { a.Error = error; a.Finished.Set(); }
            return;
        }
        if (!_active.TryGetValue(id, out var t)) return;
        t.Error = error;
        t.Finished.Set();
    }

    public static string FormatSize(long b)
    {
        if (b < 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }
}

/// <summary>One side of the file manager: path bar and a details list.</summary>
public sealed class FilePane : Panel
{
    readonly ListView _list;
    readonly BoxedTextBox _path;
    readonly bool _remote;
    FileListing _listing;

    public string CurrentPath => _listing?.Path ?? "";
    public event Action<string> NavigateRequested;
    public event Action TransferRequested;

    static ImageList _images;

    public FilePane(string title, bool remote)
    {
        _remote = remote;
        BackColor = Color.White;
        Padding = new Padding(Theme.S(12));
        Margin = new Padding(0);

        var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(40) };
        var t = Ctl.Label(title, 14, Theme.Heading);
        t.Location = new Point(0, Theme.S(2));
        header.Controls.Add(t);

        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.S(40), Padding = new Padding(0, Theme.S(2), 0, Theme.S(6)) };
        var upBtn = new IconButton(Glyph.Up, "Parent folder") { Dock = DockStyle.Left, Width = Theme.S(34) };
        upBtn.Click += (_, _) => NavigateRequested?.Invoke(_listing?.Parent ?? "");
        var refresh = new IconButton(Glyph.Refresh, "Refresh") { Dock = DockStyle.Right, Width = Theme.S(34) };
        refresh.Click += (_, _) => Reload();
        _path = new BoxedTextBox("Path", 10) { Dock = DockStyle.Fill };
        _path.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NavigateRequested?.Invoke(_path.Text.Trim()); } };
        bar.Controls.Add(_path);
        bar.Controls.Add(upBtn);
        bar.Controls.Add(refresh);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui(10),
            SmallImageList = Images,
            HideSelection = false,
        };
        _list.Columns.Add("Name", Theme.S(260));
        _list.Columns.Add("Size", Theme.S(90), HorizontalAlignment.Right);
        _list.Columns.Add("Modified", Theme.S(140));
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) OpenSelected();
            else if (e.KeyCode == Keys.Back) NavigateRequested?.Invoke(_listing?.Parent ?? "");
            else if (e.KeyCode == Keys.F5) Reload();
        };
        var menu = Ctl.Menu();
        menu.Items.Add(Ctl.Item(remote ? "Download" : "Upload", () => TransferRequested?.Invoke(), remote ? Glyph.Download : Glyph.Upload));
        menu.Items.Add(Ctl.Item("Refresh", Reload, Glyph.Refresh));
        _list.ContextMenuStrip = menu;
        _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(Theme.S(140), _list.ClientSize.Width - Theme.S(240));

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(header);
    }

    static ImageList Images
    {
        get
        {
            if (_images != null) return _images;
            _images = new ImageList { ImageSize = new Size(Theme.S(18), Theme.S(18)), ColorDepth = ColorDepth.Depth32Bit };
            _images.Images.Add("dir", Ctl.GlyphImage(Glyph.Folder, Color.FromArgb(0xE8, 0xA3, 0x17), 18));
            _images.Images.Add("file", Ctl.GlyphImage(Glyph.Document, Theme.SubText, 18));
            _images.Images.Add("drive", Ctl.GlyphImage("", Theme.SubText, 18));
            return _images;
        }
    }

    public List<FileEntry> Selected => _list.SelectedItems.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag).Where(e => e != null).ToList();

    public void Reload() => NavigateRequested?.Invoke(CurrentPath);

    public void SetBusy()
    {
        _list.Items.Clear();
        _list.Items.Add(new ListViewItem("Loading…"));
    }

    public void SetListing(FileListing l)
    {
        _listing = l;
        _path.Text = string.IsNullOrEmpty(l.Path) ? "" : l.Path;
        _path.Box.PlaceholderText = "This PC";
        _list.BeginUpdate();
        _list.Items.Clear();
        if (l.Error != null) _list.Items.Add(new ListViewItem("⚠ " + l.Error));
        bool root = string.IsNullOrEmpty(l.Path);
        foreach (var e in l.Entries.OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(e.Name, root ? "drive" : e.Dir ? "dir" : "file") { Tag = e };
            item.SubItems.Add(e.Dir ? "" : FileManagerControl.FormatSize(e.Size));
            item.SubItems.Add(e.Modified == default ? "" : e.Modified.ToString("g"));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    void OpenSelected()
    {
        var e = Selected.FirstOrDefault();
        if (e == null) return;
        if (e.Dir) NavigateRequested?.Invoke(string.IsNullOrEmpty(CurrentPath) ? e.Name : Path.Combine(CurrentPath, e.Name));
        else TransferRequested?.Invoke();
    }
}
