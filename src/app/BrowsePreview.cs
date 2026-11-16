// Hover previews on the Following page (the brief's "optional channel previews on hover"): resting the mouse on a live
// card for PreviewDelay asks for a preview of it (PreviewWanted, with the card's picture area); moving away, scrolling,
// a new list or the page going away ends it (PreviewGone). LiteView plays it (src/app/LiteView.Preview.cs).
using System;
using System.Drawing;
using LiteViewCore;

partial class BrowsePage
{
    public event Action<string, Rectangle> PreviewWanted;
    public event Action PreviewGone;
    public bool Previews = true;   // setting "previews"
    const int PreviewDelay = 900;
    readonly System.Windows.Forms.Timer previewDelay = new System.Windows.Forms.Timer { Interval = PreviewDelay };
    int previewItem = -1; bool previewShown; bool previewInit;

    // OnMouseMove: the card under the mouse
    void PreviewHover(int i)
    {
        if (!previewInit) { previewInit = true; previewDelay.Tick += (s, e) => PreviewNow(); }
        if (i == previewItem) return;
        CancelPreview();
        if (!Previews || i < 0 || i >= items.Count) return;
        var it = items[i];
        if (!it.Card || !it.Live || it.Cat != null || it.Video != null || !ChannelName.IsValidLogin(it.Login)) return;
        previewItem = i;
        previewDelay.Start();
    }

    void PreviewNow()
    {
        previewDelay.Stop();
        if (previewItem < 0 || previewItem >= items.Count || previewItem != hover || !Active || dragging || Message() != null) return;
        var r = ItemRect(previewItem);
        var tr = new Rectangle(r.X, r.Y, r.Width, r.Width * 9 / 16);
        if (tr.Top < HeadH || tr.Bottom > ClientSize.Height) return;   // only a card wholly in view
        previewShown = true;
        if (PreviewWanted != null) PreviewWanted(items[previewItem].Login, tr);
    }

    void CancelPreview()
    {
        previewDelay.Stop();
        previewItem = -1;
        if (!previewShown) return;
        previewShown = false;
        if (PreviewGone != null) PreviewGone();
    }

    /// <summary>For the UI fixture: a preview of the first card, as if the mouse rested on it.</summary>
    public bool PreviewFirst()
    {
        if (items.Count == 0 || !items[0].Card || !items[0].Live || Message() != null) return false;
        hover = 0; previewItem = 0; PreviewNow();
        return previewShown;
    }
}
