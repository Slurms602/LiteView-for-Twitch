// Screen-reader access for the custom-drawn lists (UI-4): the Following page, the followed channels column and the
// chat messages are drawn by hand, so accessibility tools saw nothing in them. One ListModel per list feeds both
// interfaces: MSAA (ListAccessible: NVDA, JAWS) and UI Automation (UiaList: Narrator, which does not read WinForms'
// MSAA objects of custom controls). Each list is a List of ListItems with name, bounds, selected / focused state,
// next / previous, and Watch as the default (Invoke) action. CI reads both (tools/a11y-dump.ps1).
using System;
using System.Drawing;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

sealed class ListModel
{
    public Control Owner;
    public Func<string> Name;
    public Func<int> Count, Selected;
    public Func<int, string> Item;
    public Func<int, Rectangle> Bounds = i => Rectangle.Empty;   // client coordinates; Empty when not known
    public Action<int> Open;
    public string Action = "Watch";

    public Rectangle ScreenBounds(int i)
    {
        var r = Bounds(i);
        if (r.IsEmpty || !Owner.IsHandleCreated) return Rectangle.Empty;
        r.Intersect(Owner.ClientRectangle);
        return r.IsEmpty ? Rectangle.Empty : Owner.RectangleToScreen(r);
    }

    public bool Offscreen(int i) { var r = Bounds(i); return !r.IsEmpty && !r.IntersectsWith(Owner.ClientRectangle); }

    /// <summary>Exposes the list to MSAA and UI Automation. Call once, from the control's constructor.</summary>
    public UiaList Attach() { var u = new UiaList(this); u.Hook(); return u; }
}

sealed class ListAccessible : Control.ControlAccessibleObject
{
    readonly ListModel m;
    public ListAccessible(ListModel model) : base(model.Owner) { m = model; }

    public override AccessibleRole Role { get { return AccessibleRole.List; } }
    public override string Name { get { return m.Name(); } set { } }
    public override int GetChildCount() { return m.Count(); }
    public override AccessibleObject GetChild(int index) { return index >= 0 && index < m.Count() ? new Row(this, index) : null; }

    public override AccessibleObject GetFocused()
    {
        int s = m.Selected();
        return m.Owner.Focused && s >= 0 && s < m.Count() ? new Row(this, s) : base.GetFocused();
    }

    public override AccessibleObject GetSelected() { int s = m.Selected(); return s >= 0 && s < m.Count() ? new Row(this, s) : null; }

    public override AccessibleObject HitTest(int x, int y)
    {
        var p = m.Owner.PointToClient(new Point(x, y));
        for (int i = 0, n = m.Count(); i < n; i++) { var r = m.Bounds(i); if (!r.IsEmpty && r.Contains(p)) return new Row(this, i); }
        return base.HitTest(x, y);
    }

    sealed class Row : AccessibleObject
    {
        readonly ListAccessible list; readonly int index;
        public Row(ListAccessible list, int index) { this.list = list; this.index = index; }
        ListModel M { get { return list.m; } }

        public override string Name { get { return index < M.Count() ? M.Item(index) : ""; } set { } }
        public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
        public override AccessibleObject Parent { get { return list; } }
        public override string DefaultAction { get { return M.Open != null ? M.Action : null; } }
        public override void DoDefaultAction() { if (M.Open != null && index < M.Count()) M.Open(index); }
        public override Rectangle Bounds { get { return M.ScreenBounds(index); } }
        public override int GetChildCount() { return 0; }

        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Selectable | AccessibleStates.Focusable | AccessibleStates.ReadOnly;
                if (M.Selected() == index) s |= AccessibleStates.Selected | (M.Owner.Focused ? AccessibleStates.Focused : 0);
                if (M.Offscreen(index)) s |= AccessibleStates.Offscreen;
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
    }
}

// UI Automation provider of a ListModel, answered from the control's own window (WM_GETOBJECT, UiaRootObjectId).
sealed class UiaList : NativeWindow, IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot, ISelectionProvider
{
    const int WM_GETOBJECT = 0x003D;
    readonly ListModel m;
    public UiaList(ListModel model) { m = model; }

    public void Hook()
    {
        if (m.Owner.IsHandleCreated) AssignHandle(m.Owner.Handle);
        m.Owner.HandleCreated += (s, e) => AssignHandle(m.Owner.Handle);
        m.Owner.HandleDestroyed += (s, e) => ReleaseHandle();
    }

    protected override void WndProc(ref Message msg)
    {
        if (msg.Msg == WM_GETOBJECT && msg.LParam.ToInt64() == AutomationInteropProvider.RootObjectId)
        {
            msg.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, msg.WParam, msg.LParam, this);
            return;
        }
        base.WndProc(ref msg);
    }

    /// <summary>The selection moved: tell UI Automation clients (Narrator follows it).</summary>
    public void Selected(int index)
    {
        if (!AutomationInteropProvider.ClientsAreListening || index < 0 || index >= m.Count() || !m.Owner.IsHandleCreated) return;
        var item = new Item(this, index);
        AutomationInteropProvider.RaiseAutomationEvent(SelectionItemPatternIdentifiers.ElementSelectedEvent, item, new AutomationEventArgs(SelectionItemPatternIdentifiers.ElementSelectedEvent));
        if (m.Owner.Focused)
            AutomationInteropProvider.RaiseAutomationEvent(AutomationElementIdentifiers.AutomationFocusChangedEvent, item, new AutomationEventArgs(AutomationElementIdentifiers.AutomationFocusChangedEvent));
    }

    // ---- the list ----
    public ProviderOptions ProviderOptions { get { return ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading; } }
    public IRawElementProviderSimple HostRawElementProvider { get { return m.Owner.IsHandleCreated ? AutomationInteropProvider.HostProviderFromHandle(m.Owner.Handle) : null; } }
    public object GetPatternProvider(int patternId) { return patternId == SelectionPatternIdentifiers.Pattern.Id ? this : null; }

    public object GetPropertyValue(int id)
    {
        if (id == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.List.Id;
        if (id == AutomationElementIdentifiers.NameProperty.Id) return m.Name();
        if (id == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return m.Owner.CanFocus;
        if (id == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id) return m.Owner.Focused;
        return null;
    }

    public IRawElementProviderFragment Navigate(NavigateDirection direction)
    {
        int n = m.Count();
        if (direction == NavigateDirection.FirstChild && n > 0) return new Item(this, 0);
        if (direction == NavigateDirection.LastChild && n > 0) return new Item(this, n - 1);
        return null;   // parent and siblings come from the window (host provider)
    }

    public int[] GetRuntimeId() { return null; }   // the host window gives the root its id
    public System.Windows.Rect BoundingRectangle { get { return System.Windows.Rect.Empty; } }   // from the host window
    public IRawElementProviderSimple[] GetEmbeddedFragmentRoots() { return null; }
    public void SetFocus() { m.Owner.Focus(); }
    public IRawElementProviderFragmentRoot FragmentRoot { get { return this; } }

    public IRawElementProviderFragment ElementProviderFromPoint(double x, double y)
    {
        var p = m.Owner.PointToClient(new Point((int)x, (int)y));
        for (int i = 0, n = m.Count(); i < n; i++) { var r = m.Bounds(i); if (!r.IsEmpty && r.Contains(p)) return new Item(this, i); }
        return this;
    }

    public IRawElementProviderFragment GetFocus()
    {
        int s = m.Selected();
        return m.Owner.Focused && s >= 0 && s < m.Count() ? new Item(this, s) : null;
    }

    public IRawElementProviderSimple[] GetSelection() { int s = m.Selected(); return s >= 0 && s < m.Count() ? new IRawElementProviderSimple[] { new Item(this, s) } : new IRawElementProviderSimple[0]; }
    public bool CanSelectMultiple { get { return false; } }
    public bool IsSelectionRequired { get { return false; } }

    // ---- one item ----
    sealed class Item : IRawElementProviderSimple, IRawElementProviderFragment, IInvokeProvider, ISelectionItemProvider
    {
        readonly UiaList list; readonly int index;
        public Item(UiaList list, int index) { this.list = list; this.index = index; }
        ListModel M { get { return list.m; } }

        public ProviderOptions ProviderOptions { get { return ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading; } }
        public IRawElementProviderSimple HostRawElementProvider { get { return null; } }

        public object GetPatternProvider(int patternId)
        {
            if (patternId == SelectionItemPatternIdentifiers.Pattern.Id) return this;
            if (patternId == InvokePatternIdentifiers.Pattern.Id && M.Open != null) return this;
            return null;
        }

        public object GetPropertyValue(int id)
        {
            if (id == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.ListItem.Id;
            if (id == AutomationElementIdentifiers.NameProperty.Id) return index < M.Count() ? M.Item(index) : "";
            if (id == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return true;
            if (id == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id) return M.Owner.Focused && M.Selected() == index;
            if (id == AutomationElementIdentifiers.IsOffscreenProperty.Id) return M.Offscreen(index);
            return null;
        }

        public IRawElementProviderFragment Navigate(NavigateDirection direction)
        {
            switch (direction)
            {
                case NavigateDirection.Parent: return list;
                case NavigateDirection.NextSibling: return index + 1 < M.Count() ? new Item(list, index + 1) : null;
                case NavigateDirection.PreviousSibling: return index > 0 ? new Item(list, index - 1) : null;
                default: return null;
            }
        }

        public int[] GetRuntimeId() { return new[] { AutomationInteropProvider.AppendRuntimeId, index }; }

        public System.Windows.Rect BoundingRectangle
        {
            get { var r = M.ScreenBounds(index); return r.IsEmpty ? System.Windows.Rect.Empty : new System.Windows.Rect(r.X, r.Y, r.Width, r.Height); }
        }

        public IRawElementProviderSimple[] GetEmbeddedFragmentRoots() { return null; }
        public void SetFocus() { M.Owner.Focus(); }
        public IRawElementProviderFragmentRoot FragmentRoot { get { return list; } }

        public void Invoke() { if (M.Open != null && index < M.Count()) M.Open(index); }
        public void Select() { }   // selection follows the keyboard; reading does not change it
        public void AddToSelection() { }
        public void RemoveFromSelection() { }
        public bool IsSelected { get { return M.Selected() == index; } }
        public IRawElementProviderSimple SelectionContainer { get { return list; } }
    }
}
