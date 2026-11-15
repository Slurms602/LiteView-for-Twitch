// Dark context menus for the fork's own controls (same colours as the chat panel's menu in src/chat.cs).
using System.Drawing;
using System.Windows.Forms;

sealed class DarkMenuColors : ProfessionalColorTable
{
    static readonly Color Back = Color.FromArgb(31, 31, 35), Line = Color.FromArgb(53, 53, 59);
    public override Color ToolStripDropDownBackground { get { return Back; } }
    public override Color MenuItemSelected { get { return Line; } }
    public override Color MenuItemBorder { get { return Line; } }
    public override Color MenuBorder { get { return Line; } }
    public override Color ImageMarginGradientBegin { get { return Back; } }
    public override Color ImageMarginGradientMiddle { get { return Back; } }
    public override Color ImageMarginGradientEnd { get { return Back; } }
    public override Color SeparatorDark { get { return Line; } }
    public override Color CheckBackground { get { return Line; } }
    public override Color CheckSelectedBackground { get { return Line; } }
    public override Color CheckPressedBackground { get { return Line; } }

    public static void Apply(ToolStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.ForeColor = Color.FromArgb(239, 239, 241);
    }
}
