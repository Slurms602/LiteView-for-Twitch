// Screen-reader access for the custom-drawn lists (UI-4): the Following page, the followed channels column and the
// chat messages are drawn by hand, so Windows' accessibility tools saw nothing in them. This exposes each list as a
// List with ListItems (name, bounds, selected / focused state, "Watch" as the default action, next / previous).
using System;
using System.Drawing;
using System.Windows.Forms;

sealed class ListAccessible : Control.ControlAccessibleObject
{
    readonly Control owner;
    readonly Func<string> name;
    readonly Func<int> count, selected;
    readonly Func<int, string> item;
    readonly Func<int, Rectangle> bounds;   // client coordinates; Rectangle.Empty when not known
    readonly Action<int> open;
    readonly string action;

    public ListAccessible(Control owner, Func<string> name, Func<int> count, Func<int, string> item, Func<int, Rectangle> bounds,
        Func<int> selected, Action<int> open, string action = "Watch") : base(owner)
    {
        this.owner = owner; this.name = name; this.count = count; this.item = item; this.bounds = bounds; this.selected = selected; this.open = open; this.action = action;
    }

    public override AccessibleRole Role { get { return AccessibleRole.List; } }
    public override string Name { get { return name(); } set { } }
    public override int GetChildCount() { return count(); }
    public override AccessibleObject GetChild(int index) { return index >= 0 && index < count() ? new Row(this, index) : null; }

    public override AccessibleObject GetFocused()
    {
        int s = selected();
        return owner.Focused && s >= 0 && s < count() ? new Row(this, s) : base.GetFocused();
    }

    public override AccessibleObject GetSelected()
    {
        int s = selected();
        return s >= 0 && s < count() ? new Row(this, s) : null;
    }

    public override AccessibleObject HitTest(int x, int y)
    {
        var p = owner.PointToClient(new Point(x, y));
        for (int i = 0, n = count(); i < n; i++)
        {
            var r = bounds(i);
            if (!r.IsEmpty && r.Contains(p)) return new Row(this, i);
        }
        return base.HitTest(x, y);
    }

    sealed class Row : AccessibleObject
    {
        readonly ListAccessible list; readonly int index;
        public Row(ListAccessible list, int index) { this.list = list; this.index = index; }

        public override string Name { get { return index < list.count() ? list.item(index) : ""; } set { } }
        public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
        public override AccessibleObject Parent { get { return list; } }
        public override string DefaultAction { get { return list.open != null ? list.action : null; } }
        public override void DoDefaultAction() { if (list.open != null && index < list.count()) list.open(index); }

        public override Rectangle Bounds
        {
            get
            {
                var r = list.bounds(index);
                if (r.IsEmpty) return Rectangle.Empty;
                r.Intersect(list.owner.ClientRectangle);
                return r.IsEmpty ? Rectangle.Empty : list.owner.RectangleToScreen(r);
            }
        }

        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Selectable | AccessibleStates.Focusable | AccessibleStates.ReadOnly;
                if (list.selected() == index) s |= AccessibleStates.Selected | (list.owner.Focused ? AccessibleStates.Focused : 0);
                var r = list.bounds(index);
                if (!r.IsEmpty && !r.IntersectsWith(list.owner.ClientRectangle)) s |= AccessibleStates.Offscreen;
                return s;
            }
        }

        public override AccessibleObject Navigate(AccessibleNavigation dir)
        {
            switch (dir)
            {
                case AccessibleNavigation.Next: case AccessibleNavigation.Down: return list.GetChild(index + 1);
                case AccessibleNavigation.Previous: case AccessibleNavigation.Up: return list.GetChild(index - 1);
                case AccessibleNavigation.FirstChild: case AccessibleNavigation.LastChild: return null;
                default: return base.Navigate(dir);
            }
        }

        public override int GetChildCount() { return 0; }
    }
}
