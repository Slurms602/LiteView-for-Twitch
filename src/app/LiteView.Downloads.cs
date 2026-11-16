// Downloads (src/core/DownloadCommand.cs): a video's or clip's right-click menu > Download > quality. Streamlink writes it
// to Videos\LiteView Downloads (".part" until it ended well), at most two at a time (more wait their turn), each in its
// own kill-on-close job at below-normal priority. The Downloads window (menu > Downloads...) lists them with their
// progress (from Streamlink's log) and size, Cancel, Show in folder. Closing LiteView stops running downloads (it asks
// first); a stopped download's ".part" file is deleted.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using LiteViewCore;

sealed class Download
{
    public VideoInfo Video; public string Quality = "", File = "", Part = "", Log = "";
    public IntPtr Job; public Process Proc;
    public readonly DownloadProgress Progress = new DownloadProgress();
    public long LogRead, Bytes, Estimate;
    public string State = "Waiting";   // Waiting, Downloading, Done, Failed, Cancelled
    public string Error = "";
    public bool Running { get { return State == "Downloading"; } }
    public bool Finished { get { return State == "Done" || State == "Failed" || State == "Cancelled"; } }
}

partial class LiteView
{
    const int MaxDownloads = 2;
    readonly List<Download> downloads = new List<Download>();
    readonly System.Windows.Forms.Timer downloadTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    DownloadsWindow downloadsWin;
    bool downloadsInit;

    static string DownloadFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiteView Downloads"); } }

    void InitDownloads()
    {
        if (downloadsInit) return;
        downloadsInit = true;
        browse.DownloadWanted += StartDownload;
        downloadTimer.Tick += (s, e) => DownloadTick();
        FormClosing += (s, e) =>
        {
            int n = downloads.Count(d => d.Running || d.State == "Waiting");
            if (n == 0 || e.Cancel || Fixture.On) { StopAllDownloads(); return; }
            if (MessageBox.Show(this, n + (n == 1 ? " download is" : " downloads are") + " not finished. Closing LiteView stops " + (n == 1 ? "it" : "them") + " (the unfinished file is deleted). Close anyway?",
                "Downloads", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
            StopAllDownloads();
        };
    }

    void StartDownload(VideoInfo v, string quality)
    {
        if (v == null || Array.IndexOf(DownloadCommand.Qualities, quality) < 0) return;
        if (MissingPlayerFiles() != null) { ShowProblem("Could not download", MissingPlayerFiles(), null); return; }
        try { Directory.CreateDirectory(DownloadFolder); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { ShowProblem("Could not download", "The folder " + DownloadFolder + " could not be made: " + ex.Message, null); return; }
        string name = DownloadCommand.FileName(v.Login, v.Title, v.Created, v.IsClip), file = Path.Combine(DownloadFolder, name);
        for (int i = 2; File.Exists(file) || File.Exists(file + ".part") || downloads.Any(d => !d.Finished && d.File == file); i++)
            file = Path.Combine(DownloadFolder, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
        long est = v.IsClip ? 0 : DownloadCommand.EstimateBytes(v.Seconds, quality);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(DownloadFolder));
            if (est > 0 && drive.AvailableFreeSpace < est + (1L << 30)
                && MessageBox.Show(this, "This download is about " + DownloadCommand.Size(est) + " and " + drive.Name + " has " + DownloadCommand.Size(drive.AvailableFreeSpace) + " free. Download anyway?",
                    "Download", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { }
        var d = new Download { Video = v, Quality = quality, File = file, Part = file + ".part", Estimate = est };
        downloads.Add(d);
        Log.Info("download: " + (v.IsClip ? "clip " : "video ") + v.Id + " (" + quality + ")");
        DownloadTick();
        ShowDownloads();
    }

    void DownloadTick()
    {
        foreach (var d in downloads.Where(x => x.Running).ToList()) CheckDownload(d);
        while (downloads.Count(x => x.Running) < MaxDownloads)
        {
            var next = downloads.FirstOrDefault(x => x.State == "Waiting");
            if (next == null) break;
            RunDownload(next);
        }
        downloadTimer.Enabled = downloads.Any(x => x.Running || x.State == "Waiting");
        if (downloadsWin != null && !downloadsWin.IsDisposed) downloadsWin.ShowList(downloads);
    }

    void RunDownload(Download d)
    {
        int slot = 1;
        while (downloads.Any(x => x.Running && x.Log.EndsWith("-download" + slot + ".log", StringComparison.Ordinal))) slot++;
        d.Log = Path.Combine(Path.GetDirectoryName(slLog), Path.GetFileNameWithoutExtension(slLog) + "-download" + slot + ".log");
        try { File.Delete(d.Log); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string cmd;
        try
        {
            cmd = DownloadCommand.Build(new DownloadRequest { Streamlink = streamlink, LogFile = d.Log, Output = d.Part, Channel = d.Video.Login,
                Video = d.Video.IsClip ? "" : d.Video.Id, Clip = d.Video.IsClip ? d.Video.Id : "", Quality = d.Quality });
        }
        catch (ArgumentException ex) { d.State = "Failed"; d.Error = ex.Message; Log.Warn("download: " + ex.Message); return; }
        var r = RunInJob(cmd, Native.BELOW_NORMAL);
        if (r.Key == IntPtr.Zero) { d.State = "Failed"; d.Error = "Streamlink could not be started"; Log.Warn("download: Streamlink could not be started"); return; }
        d.Job = r.Key; d.Proc = r.Value; d.State = "Downloading"; d.LogRead = 0;
        downloadTimer.Start();
    }

    void CheckDownload(Download d)
    {
        // new log lines since the last look (the log grows while Streamlink writes)
        try
        {
            if (File.Exists(d.Log))
                using (var fs = new FileStream(d.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > d.LogRead)
                    {
                        fs.Seek(d.LogRead, SeekOrigin.Begin);
                        int n = (int)Math.Min(fs.Length - d.LogRead, 4 << 20);
                        var buf = new byte[n]; int got = fs.Read(buf, 0, n);
                        string text = Encoding.UTF8.GetString(buf, 0, got);
                        int end = text.LastIndexOf('\n');
                        if (end >= 0) { DownloadCommand.Read(d.Progress, text.Substring(0, end).Split('\n')); d.LogRead += Encoding.UTF8.GetByteCount(text.Substring(0, end + 1)); }
                    }
                }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        try { if (File.Exists(d.Part)) d.Bytes = new FileInfo(d.Part).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        bool alive;
        try { alive = !d.Proc.HasExited; } catch (InvalidOperationException) { alive = false; }
        if (alive) return;
        int code = -1;
        try { code = d.Proc.ExitCode; } catch (InvalidOperationException) { }
        EndJob(d);
        if (code == 0 && d.Bytes > 0)
        {
            try { File.Move(d.Part, d.File); d.State = "Done"; Log.Info("download: done (" + DownloadCommand.Size(d.Bytes) + ")"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { d.State = "Failed"; d.Error = "could not rename the file: " + ex.Message; }
        }
        else
        {
            string err = "";
            try { err = StreamOutcome.LastError(ReadShared(d.Log)); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            d.State = "Failed"; d.Error = err.Length > 0 ? err : "Streamlink ended (" + code + ")";
            Log.Warn("download: failed (" + d.Error + ")");
            DeletePart(d);
        }
    }

    void CancelDownload(Download d)
    {
        if (d.Finished) return;
        EndJob(d);
        d.State = "Cancelled";
        DeletePart(d);
        Log.Info("download: cancelled");
        DownloadTick();
    }

    void EndJob(Download d)
    {
        if (d.Job != IntPtr.Zero) { TerminateJobObject(d.Job, 0); Native.CloseHandle(d.Job); d.Job = IntPtr.Zero; }
        if (d.Proc != null) { d.Proc.Dispose(); d.Proc = null; }
    }

    static void DeletePart(Download d)
    {
        for (int i = 0; i < 5; i++)   // Streamlink may still hold it for a moment after its job ended
        {
            try { if (File.Exists(d.Part)) File.Delete(d.Part); return; }
            catch (IOException) { System.Threading.Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    void StopAllDownloads()
    {
        foreach (var d in downloads.Where(x => !x.Finished).ToList()) { EndJob(d); d.State = "Cancelled"; DeletePart(d); }
        downloadTimer.Stop();
    }

    void ShowDownloads()
    {
        if (downloadsWin == null || downloadsWin.IsDisposed)
        {
            downloadsWin = new DownloadsWindow(CancelDownload, d => { if (File.Exists(d.File)) Process.Start("explorer.exe", "/select,\"" + d.File + "\""); else OpenDownloadFolder(); }, OpenDownloadFolder,
                () => { downloads.RemoveAll(x => x.Finished); DownloadTick(); }) { Icon = Icon };
            downloadsWin.Show(this);
        }
        else downloadsWin.Activate();
        downloadsWin.ShowList(downloads);
    }

    void OpenDownloadFolder()
    {
        try { Directory.CreateDirectory(DownloadFolder); Process.Start("explorer.exe", "\"" + DownloadFolder + "\""); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { Log.Warn("download folder: " + ex.GetType().Name); }
    }

    // UI fixture (CI), scene "download": the first video of the made-up channel, at 480p, through tests/FakeStreamlink
    void DownloadScene()
    {
        browse.ShowVideos("nightowl_plays", "NightowlPlays", false);
        var t = new Timer { Interval = 500 }; int waited = 0;
        t.Tick += (s, e) => { waited += 500; if (!browse.DownloadFirstVideo() && waited < 10000) return; t.Stop(); t.Dispose(); };
        t.Start();
    }
}

// The Downloads window: one row per download with its progress, Cancel or Show in folder; Clear finished; Open folder.
sealed class DownloadsWindow : Form
{
    readonly Action<Download> cancel, show; readonly Action openFolder, clear;
    readonly Rows rows = new Rows();
    readonly Panel foot = new Panel { Dock = DockStyle.Bottom, Height = 44 };
    readonly Button folderBtn = new Button(), clearBtn = new Button();

    public DownloadsWindow(Action<Download> cancel, Action<Download> show, Action openFolder, Action clear)
    {
        this.cancel = cancel; this.show = show; this.openFolder = openFolder; this.clear = clear;
        Text = "Downloads  -  LiteView"; ClientSize = new Size(620, 340); MinimumSize = new Size(420, 220); StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel; ForeColor = Theme.Text; ShowInTaskbar = false; Font = UiFont.ForControls(FontKind.Text, 9.5f);
        foreach (var b in new[] { folderBtn, clearBtn })
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.BackColor = Theme.Line; b.ForeColor = Theme.Text; b.Height = 30; b.Cursor = Cursors.Hand; b.Top = 7;
            foot.Controls.Add(b);
        }
        folderBtn.Text = "Open the downloads folder"; folderBtn.Width = 190; folderBtn.Left = 12; folderBtn.Click += (s, e) => openFolder();
        clearBtn.Text = "Clear finished"; clearBtn.Width = 120; clearBtn.Left = folderBtn.Right + 8; clearBtn.Click += (s, e) => clear();
        rows.Dock = DockStyle.Fill; rows.Act = (d, which) => { if (which == 0) cancel(d); else show(d); };
        Controls.Add(rows); Controls.Add(foot);
        HandleCreated += (s, e) => Theme.Frame(Handle);
    }

    public void ShowList(List<Download> list) { rows.Set(list); }

    sealed class Rows : Control
    {
        List<Download> list = new List<Download>();
        readonly List<KeyValuePair<Rectangle, KeyValuePair<Download, int>>> hits = new List<KeyValuePair<Rectangle, KeyValuePair<Download, int>>>();
        public Action<Download, int> Act;   // 0 cancel, 1 show in folder
        const int RowH = 70;

        public Rows() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Theme.Panel; }

        public void Set(List<Download> l) { list = l.ToList(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; hits.Clear();
            var f = UiFont.Get(FontKind.Semi, 10f); var fs = UiFont.Get(FontKind.Text, 9f);
            if (list.Count == 0) { Txt.In(g, "Nothing downloaded yet: right-click a video or clip on the Following page > Download.", fs, Theme.Dim, new Rectangle(16, 12, Width - 32, 40), false); return; }
            int y = 8;
            foreach (var d in list)
            {
                if (y > Height) break;
                var v = d.Video;
                string title = (v.Title.Length > 0 ? v.Title : VideoData.KindName(v.Kind)) + "  ·  " + v.Name;
                Txt.In(g, title, f, Theme.Text, new Rectangle(16, y, Width - 150, 22), false);
                double frac = d.State == "Done" ? 1 : d.Progress.Fraction;
                string size = d.Bytes > 0 ? DownloadCommand.Size(d.Bytes) : "";
                string info = d.State == "Downloading" ? (frac >= 0 ? ((int)(frac * 100)) + " %" + (size.Length > 0 ? "  ·  " + size : "") : size.Length > 0 ? size : "starting...") + (d.Estimate > 0 ? "  (about " + DownloadCommand.Size(d.Estimate) + " in all)" : "")
                    : d.State == "Done" ? "Done  ·  " + size + "  ·  " + Path.GetFileName(d.File)
                    : d.State == "Failed" ? "Failed: " + d.Error : d.State == "Cancelled" ? "Cancelled" : "Waiting (two at a time)";
                Txt.In(g, DownloadCommand.QualityName(d.Quality) + "  ·  " + info, fs, d.State == "Failed" ? Theme.Error : Theme.Dim, new Rectangle(16, y + 24, Width - 150, 20), false);
                var bar = new Rectangle(16, y + 50, Width - 150, 5);
                using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, bar);
                if (frac > 0) using (var b = new SolidBrush(d.State == "Done" ? Color.FromArgb(0, 173, 3) : Theme.Accent)) g.FillRectangle(b, bar.X, bar.Y, (int)(bar.Width * frac), bar.Height);
                string act = d.Finished ? (d.State == "Done" ? "Show in folder" : "") : "Cancel";
                if (act.Length > 0)
                {
                    var r = new Rectangle(Width - 126, y + 18, 110, 28);
                    using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, r);
                    Txt.In(g, act, fs, Theme.AccentText, r, true);
                    hits.Add(new KeyValuePair<Rectangle, KeyValuePair<Download, int>>(r, new KeyValuePair<Download, int>(d, d.Finished ? 1 : 0)));
                }
                y += RowH;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { Cursor = hits.Any(h => h.Key.Contains(e.Location)) ? Cursors.Hand : Cursors.Default; }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            foreach (var h in hits) if (h.Key.Contains(e.Location) && Act != null) { Act(h.Value.Key, h.Value.Value); return; }
        }
    }
}
