// The emote picker (src/picker.cs) by keyboard and for screen readers (UI-4). Ctrl+E in the chat box opens it; in its
// search box, Down goes into the grid. In the grid: arrow keys, Home / End, Page Up / Page Down choose an
// emote, Enter or Space puts it in the chat box (the picker stays open for more), typing goes back to the search,
// Up from the top row too, Esc closes. Screen readers get the grid as a list "Emotes" whose items are "name, set"
// (MSAA for NVDA / JAWS, UI Automation for Narrator, through the same ListModel as the other lists).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

partial class EmotePicker
{
    readonly List<int> emoteCells = new List<int>();   // indexes into cells of the emotes, in order
    readonly List<string> emoteSets = new List<string>();
    int keySel = -1;                                    // index into emoteCells; -1 = none
    string keyName;                                     // the selected emote, kept across rebuilds (images loading, search)
    ListModel a11y; UiaList uia;

    void InitAccess()
    {
        SetStyle(ControlStyles.Selectable, true); TabStop = true;
        AccessibleName = "Emotes";
        uia = Model().Attach();
        GotFocus += (s, e) =>
        {
            int h = MouseButtons != MouseButtons.None ? HitCell(PointToClient(MousePosition)) : -1;   // clicked: that emote
            int k = h >= 0 ? emoteCells.IndexOf(h) : -1;
            if (k >= 0) Select(k, true);
            else if (keySel < 0 && emoteCells.Count > 0) Select(0, true);
            else Announce();
            Invalidate();
        };
        LostFocus += (s, e) => Invalidate();
    }

    ListModel Model()
    {
        return a11y ?? (a11y = new ListModel
        {
            Owner = this, Action = "Insert",
            Name = () => emoteCells.Count > 0 ? "Emotes" : "Emotes. " + (cells.Count > 0 && cells[0].Head != null ? cells[0].Head : "none"),
            Count = () => emoteCells.Count,
            // a screen reader may ask for an item just after the list got shorter (search, images loading)
            Item = i => i >= 0 && i < emoteCells.Count ? cells[emoteCells[i]].Name + ", " + emoteSets[i] : "",
            Bounds = i => { if (i < 0 || i >= emoteCells.Count) return Rectangle.Empty; var r = cells[emoteCells[i]].R; r.Y -= top; return r; },
            Selected = () => keySel,
            Open = i => Pick(i),
        });
    }

    protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessible(Model()); }

    void AfterBuild()
    {
        emoteCells.Clear(); emoteSets.Clear();
        string set = "";
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].E == null) { set = cells[i].Head ?? ""; continue; }
            emoteCells.Add(i); emoteSets.Add(set);
        }
        keySel = -1;
        if (keyName != null) for (int k = 0; k < emoteCells.Count; k++) if (cells[emoteCells[k]].Name == keyName) { keySel = k; break; }
        if (keySel < 0 && Focused && emoteCells.Count > 0) { keySel = 0; keyName = cells[emoteCells[0]].Name; }
    }

    void PaintKey(Graphics g, int cell, Rectangle r)
    {
        if (!Focused || keySel < 0 || keySel >= emoteCells.Count || emoteCells[keySel] != cell) return;
        using (var p = new Pen(ChatView.Accent, 2f)) g.DrawRectangle(p, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
    }

    // the search box: Down into the grid (true = handled)
    bool SearchKey(KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Down || e.Control || e.Alt || e.Shift || emoteCells.Count == 0) return false;
        e.SuppressKeyPress = true; e.Handled = true;
        if (keySel < 0) { keySel = 0; keyName = cells[emoteCells[0]].Name; }
        Focus();
        return true;
    }

    protected override bool IsInputKey(Keys k)
    {
        switch (k & Keys.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End:
            case Keys.PageUp: case Keys.PageDown: case Keys.Enter: case Keys.Space: case Keys.Escape: return true;
        }
        return base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int n = emoteCells.Count, s = keySel;
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.Enter: case Keys.Space: if (s >= 0) Pick(s); break;
            case Keys.Back: Search.Focus(); if (Search.Text.Length > 0) { Search.Text = Search.Text.Substring(0, Search.Text.Length - 1); Search.SelectionStart = Search.Text.Length; } break;
            case Keys.Left: if (n > 0) Select(Math.Max(0, s - 1), false); break;
            case Keys.Right: if (n > 0) Select(Math.Min(n - 1, s + 1), false); break;
            case Keys.Home: if (n > 0) Select(0, false); break;
            case Keys.End: if (n > 0) Select(n - 1, false); break;
            case Keys.Up: { int t = RowStep(s, -1); if (t < 0) Search.Focus(); else Select(t, false); break; }
            case Keys.Down: { int t = RowStep(s, 1); if (t >= 0) Select(t, false); break; }
            case Keys.PageUp: case Keys.PageDown:
            {
                int t = s, rows = Math.Max(1, (ClientSize.Height - Top0) / Ch - 1);
                for (int i = 0; i < rows; i++) { int u = RowStep(t, e.KeyCode == Keys.PageUp ? -1 : 1); if (u < 0) break; t = u; }
                if (t >= 0) Select(t, false);
                break;
            }
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }

    // typing in the grid goes on in the search box
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar) || e.KeyChar == ' ') return;
        Search.Focus(); Search.SelectionStart = Search.Text.Length; Search.SelectionLength = 0; Search.SelectedText = e.KeyChar.ToString();
        e.Handled = true;
    }

    // the emote in the next row (dir 1) or the previous one (-1) nearest to the same column; -1 when there is none
    int RowStep(int s, int dir)
    {
        if (s < 0 || s >= emoteCells.Count) return emoteCells.Count > 0 && dir > 0 ? 0 : -1;
        var cur = cells[emoteCells[s]].R;
        int rowY = -1, best = -1, bestDx = int.MaxValue;
        for (int k = dir > 0 ? s + 1 : s - 1; k >= 0 && k < emoteCells.Count; k += dir)
        {
            var r = cells[emoteCells[k]].R;
            if (r.Y == cur.Y) continue;
            if (rowY < 0) rowY = r.Y;
            if (r.Y != rowY) break;
            int dx = Math.Abs(r.X - cur.X);
            if (dx < bestDx) { bestDx = dx; best = k; }
        }
        return best;
    }

    void Select(int k, bool always)
    {
        if (k < 0 || k >= emoteCells.Count || (k == keySel && !always)) return;
        keySel = k; keyName = cells[emoteCells[k]].Name;
        var r = cells[emoteCells[k]].R;
        int view = ClientSize.Height;
        if (r.Y - top < Top0) top = Math.Max(0, r.Y - Top0 - (emoteCells[k] > 0 && cells[emoteCells[k] - 1].E == null ? Hh : 0));
        else if (r.Bottom - top > view) top = r.Bottom - view + 6;
        top = Math.Max(0, Math.Min(top, Math.Max(0, total - view)));
        Invalidate(); Announce();
    }

    void Announce()   // the selection moved: screen readers follow it
    {
        if (!Focused || !IsHandleCreated || keySel < 0 || keySel >= emoteCells.Count) return;
        AccessibilityNotifyClients(AccessibleEvents.Focus, keySel);
        AccessibilityNotifyClients(AccessibleEvents.Selection, keySel);
        uia.Selected(keySel);
    }

    void Pick(int k) { if (k >= 0 && k < emoteCells.Count && Picked != null) Picked(cells[emoteCells[k]].Name); }

    /// <summary>For the UI fixture: the grid with the keyboard on its n-th emote.</summary>
    public void FocusGrid(int n) { if (emoteCells.Count == 0) return; Focus(); Select(Math.Min(n, emoteCells.Count - 1), true); }
}

partial class ChatPanel
{
    // Ctrl+E in the chat box opens or closes the emote picker; the emote button gets a name screen readers can say
    void InitPickerAccess()
    {
        emoteBtn.AccessibleName = "Emotes (Ctrl+E)";
        input.KeyDown += (s, e) => { if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.E) { e.SuppressKeyPress = true; e.Handled = true; TogglePicker(); } };
        picker.KeyDown += (s, e) => { if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.E) { e.Handled = true; TogglePicker(); } };
        picker.Search.KeyDown += (s, e) => { if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.E) { e.SuppressKeyPress = true; e.Handled = true; TogglePicker(); } };
    }

    /// <summary>For the UI fixture: the picker open with the keyboard on its n-th emote.</summary>
    public void OpenPicker(int n) { if (!picker.Visible) TogglePicker(); picker.FocusGrid(n); }
}
