// Pause (P4): the button left of the volume, Space, or the keyboard's play/pause key. A live stream cannot be held
// without downloading it, so Pause ends Streamlink and mpv: no download, no decoding, no GPU work for the video.
// The chat keeps running. Play starts the stream again at the live edge (a few seconds, like opening the channel).
using System;
using System.Drawing;
using System.Windows.Forms;
using LiteViewCore;

partial class LiteView
{
    static readonly string PauseGlyph = ((char)0xE769).ToString(), PlayGlyph = ((char)0xE768).ToString();
    readonly Button pauseBtn = new Button();
    readonly Label pausedNote = new Label();
    readonly ToolTip pauseTip = new ToolTip();
    bool paused;

    void InitPause()
    {
        Flat(pauseBtn, PauseGlyph); pauseBtn.Font = new Font("Segoe MDL2 Assets", 11f); pauseBtn.Width = 34;
        pauseBtn.AccessibleName = "Pause";
        pauseBtn.Click += (s, e) => TogglePause();
        pauseTip.SetToolTip(pauseBtn, "Pause (Space): stops the video and its download, the chat stays.\nPlay continues at live.");
        bar.Controls.Add(pauseBtn);

        pausedNote.Dock = DockStyle.Fill; pausedNote.Visible = false; pausedNote.BackColor = Color.Black; pausedNote.ForeColor = Dim;
        pausedNote.TextAlign = ContentAlignment.MiddleCenter; pausedNote.Font = new Font("Segoe UI", 12f); pausedNote.Cursor = Cursors.Hand;
        pausedNote.Text = "Paused\n\nClick here or press Space to continue at live";
        pausedNote.Click += (s, e) => TogglePause();
        video.Controls.Add(pausedNote);
    }

    void TogglePause()
    {
        if (!watching || channel == null) return;
        if (paused) { Log.Info("play"); Replay(); }
        else Pause();
    }

    void Pause()
    {
        CancelRetry(); StopStream();
        paused = true;
        pausedNote.Visible = true; pausedNote.BringToFront();
        pauseBtn.Text = PlayGlyph; pauseBtn.AccessibleName = "Play";
        pauseTip.SetToolTip(pauseBtn, "Play (Space): continues at live");
        status.ForeColor = Dim; status.Text = "Paused";
        Log.Info("paused: stream stopped");
    }

    // a stream starts (Play, another channel, a new quality) or the Following page shows
    void EndPause()
    {
        if (!paused) return;
        paused = false;
        pausedNote.Visible = false;
        pauseBtn.Text = PauseGlyph; pauseBtn.AccessibleName = "Pause";
        pauseTip.SetToolTip(pauseBtn, "Pause (Space): stops the video and its download, the chat stays.\nPlay continues at live.");
    }
}
