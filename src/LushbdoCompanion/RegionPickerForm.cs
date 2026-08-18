namespace LushbdoCompanion;

/// <summary>
/// A one-shot, whole-desktop overlay: drag a rectangle around the game's loot
/// chat tab, Esc cancels. Since the game window became the capture target this
/// is the fallback picker, used only when that window cannot be found — the
/// selection comes back in screen pixels and the caller must anchor it to the
/// game window itself. Everything here is physical screen pixels — the app
/// is per-monitor DPI aware precisely so the picked rectangle and the capture
/// frames agree without any scaling arithmetic between them.
/// </summary>
public sealed class RegionPickerForm : Form
{
    public Rectangle Selection { get; private set; }

    private Point _dragStart;
    private Rectangle _dragRect;
    private bool _dragging;

    public RegionPickerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = 0.45; // dim the desktop but keep the chat readable to aim at
        DoubleBuffered = true;

        // Windows likes to rescale a window that straddles monitors with
        // different DPIs; pin the overlay to the whole desktop regardless.
        DpiChanged += (_, _) => Bounds = SystemInformation.VirtualScreen;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate(); // so Esc lands here, not in the game
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = e.Location;
        _dragRect = new Rectangle(e.Location, Size.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _dragRect = Rectangle.FromLTRB(
            Math.Min(_dragStart.X, e.X), Math.Min(_dragStart.Y, e.Y),
            Math.Max(_dragStart.X, e.X), Math.Max(_dragStart.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;

        if (_dragRect.Width < 40 || _dragRect.Height < 16)
        {
            // A stray click, not a chat tab. Keep the overlay up and let them drag.
            _dragRect = Rectangle.Empty;
            Invalidate();
            return;
        }

        Selection = new Rectangle(_dragRect.X + Bounds.X, _dragRect.Y + Bounds.Y, _dragRect.Width, _dragRect.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        const string hint = "Drag a rectangle around the game's loot chat tab — Esc cancels";
        using var hintFont = new Font("Segoe UI", 14f, FontStyle.Bold);
        foreach (var screen in Screen.AllScreens)
        {
            var size = g.MeasureString(hint, hintFont);
            g.DrawString(hint, hintFont, Brushes.White, new PointF(
                screen.Bounds.X - Bounds.X + (screen.Bounds.Width - size.Width) / 2,
                screen.Bounds.Y - Bounds.Y + screen.Bounds.Height * 0.12f));
        }

        if (_dragRect.Width <= 0) return;
        using var pen = new Pen(Color.FromArgb(102, 217, 108), 2f);
        g.DrawRectangle(pen, _dragRect);
        using var sizeFont = new Font("Segoe UI", 10f);
        g.DrawString($"{_dragRect.Width} × {_dragRect.Height}", sizeFont, Brushes.White,
            _dragRect.X, Math.Max(0, _dragRect.Y - 22));
    }
}
