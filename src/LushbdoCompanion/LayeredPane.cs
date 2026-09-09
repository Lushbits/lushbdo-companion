using System.Drawing;
using System.Runtime.InteropServices;

namespace LushbdoCompanion;

/// <summary>
/// The window half of an overlay: a top-most, click-through, never-activated
/// layered window in our own process that shows one alpha bitmap at a point
/// on the screen, and can be picked up and dragged while a preview lasts.
/// Two of these make the overlay — the session figures and the item slots
/// (#52) — and <see cref="OverlayForm"/> is the one that decides what they
/// show and where and when; this is what the two have in common, so the
/// Win32 and the drag are written once.
///
/// How it stays in the same class as everything else here: this is a
/// separate window placed over the game window's rectangle — the same thing
/// as dragging a browser over the game. Nothing is injected, nothing is
/// hooked, nothing touches the game's process. Capture is per-window, so
/// this window never appears in the frames OCR reads.
///
/// A layered window is hit only where its pixels have alpha, which is why a
/// preview's bitmap gets a hair of alpha everywhere: the figures can be
/// grabbed between the digits too. The window follows the mouse by plain
/// moves while the button is down, and the drop is said to the owner as a
/// point, which the owner turns into a placement.
/// </summary>
public abstract class LayeredPane : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;

    private Point _dragStart;   // screen: where the mouse went down
    private Point _dragOrigin;  // screen: where the window was then

    /// <summary>The size of the bitmap last pushed, which is the window's size.</summary>
    public Size Painted { get; private set; }

    /// <summary>Where the window's top-left is on the screen — or was, if it is hidden.</summary>
    public Point ShownAt { get; private set; } = new(int.MinValue, int.MinValue);

    public bool OnScreen { get; private set; }

    /// <summary>The click-through style is off: a preview, where the bitmap can be dragged.</summary>
    public bool TakesMouse { get; private set; }

    public bool Dragging { get; private set; }

    /// <summary>A drag ended with the bitmap's top-left here, in screen pixels. The owner says it back as a placement.</summary>
    public event Action<Point>? Dropped;

    protected LayeredPane(string title)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = title;
        _ = Handle; // the layered window exists before the first bitmap arrives
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost;
            return p;
        }
    }

    /// <summary>
    /// Hand a bitmap to the compositor whole; there is no WM_PAINT and
    /// nothing for the game to redraw over. The window takes the bitmap's
    /// size and keeps its place.
    /// </summary>
    protected void Push(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memDc, hBitmap);
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = AcSrcOver, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        Painted = bitmap.Size;
    }

    /// <summary>One window move, and none when it is already there.</summary>
    public void MoveTo(Point at)
    {
        if (at == ShownAt || IsDisposed) return;
        SetWindowPos(Handle, HwndTopmost, at.X, at.Y, 0, 0, SwpNoSize | SwpNoActivate);
        ShownAt = at;
    }

    public void Reveal()
    {
        if (OnScreen || IsDisposed) return;
        ShowWindow(Handle, SwShowNoActivate);
        OnScreen = true;
    }

    public void Conceal()
    {
        if (!OnScreen || IsDisposed) return;
        ShowWindow(Handle, SwHide);
        OnScreen = false;
    }

    /// <summary>
    /// Click-through or not. The style is what makes the window passive
    /// beside the game — the mouse goes through the figures to whatever is
    /// under them — and it is dropped for exactly as long as a preview
    /// lasts, so the figures can be picked up. The cursor says so.
    /// </summary>
    public void TakeMouse(bool on)
    {
        if (on == TakesMouse || IsDisposed) return;
        TakesMouse = on;
        var style = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        style = on ? style & ~WsExTransparent : style | WsExTransparent;
        SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style));
        Cursor = on ? Cursors.SizeAll : Cursors.Default;
    }

    /// <summary>Let go of a drag without a drop — a preview ending mid-drag. The owner puts the window back on its next follow.</summary>
    public void CancelDrag()
    {
        if (Dragging) EndDrag(apply: false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !TakesMouse || !OnScreen) return;
        Dragging = true;
        _dragStart = Cursor.Position;
        _dragOrigin = ShownAt;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Dragging) return;
        var now = Cursor.Position;
        MoveTo(new Point(_dragOrigin.X + now.X - _dragStart.X, _dragOrigin.Y + now.Y - _dragStart.Y));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Dragging && e.Button == MouseButtons.Left) EndDrag(apply: true);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Dragging && !Capture) EndDrag(apply: true); // the capture was taken away mid-drag: keep where it got to
    }

    private void EndDrag(bool apply)
    {
        Dragging = false;
        if (Capture) Capture = false;
        if (apply) Dropped?.Invoke(ShownAt);
    }

    // --- Win32 -----------------------------------------------------------

    private static readonly IntPtr HwndTopmost = new(-1);
    private const int GwlExStyle = -20;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int UlwAlpha = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    // The Ptr variants: the app is built for win-x64 only, where the plain ones do not exist.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
