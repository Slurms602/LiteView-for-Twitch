// LiteView's own icon (src/app/liteview.ico, drawn by tools/make-icon.py). Upstream's windows showed mpv.exe's icon,
// so LiteView looked like an mpv window in the taskbar; mpv.exe itself is not changed (a real mpv keeps its icon).
using System.Drawing;

static class AppIcon
{
    static Icon icon;
    static bool tried;

    /// <summary>The icon in every size it has (null if the resource is missing).</summary>
    public static Icon Load()
    {
        if (tried) return icon;
        tried = true;
        try
        {
            using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("liteview.ico"))
                if (s != null) icon = new Icon(s);
        }
        catch (System.ArgumentException) { }
        if (icon == null) LiteViewCore.Log.Warn("icon: liteview.ico missing from the build");
        return icon;
    }
}
