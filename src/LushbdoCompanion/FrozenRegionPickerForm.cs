using System.Drawing.Drawing2D;

namespace LushbdoCompanion;

/// <summary>
/// The region picker, drawn on a frozen frame of the game's own window rather
/// than the live desktop: the game can sit buried under a browser and the
/// picture here is still a clean still of its loot chat. The drag maps back to
/// window-relative physical pixels — the same space the watcher crops, so what
/// gets circled is exactly what gets OCR'd, with no screen coordinates in
/// between. Esc cancels. In borderless-windowed play the still is the size of
/// the monitor and shows 1:1; a smaller windowed client is scaled up to fit
/// and the mapping divides the scale back out.
/// </summary>
public sealed class FrozenRegionPickerForm : Form
{
    /// <summary>The picked rectangle in window-relative physical pixels of the captured frame.</summary>
    public Rectangle Selection { get; private set; }

    private readonly Bitmap _still;
    private Point _dragStart;
    private Rectangle _dragRect; // display coordinates; mapped to frame pixels on mouse-up
    private bool _dragging;

    public FrozenRegionPickerForm(Bitmap still, Rectangle screenBounds)
    {
        _still = still;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        DoubleBuffered = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate(); // so Esc lands here, not in the game
    }

    private (float Scale, PointF Offset) Placement()
    {
        var scale = Math.Min((float)ClientSize.Width / _still.Width, (float)ClientSize.Height / _still.Height);
        return (scale, new PointF(
            (ClientSize.Width - _still.Width * scale) / 2f,
            (ClientSize.Height - _still.Height * scale) / 2f));
    }

    private Rectangle ToFramePixels(Rectangle display)
    {
        var (scale, offset) = Placement();
        var mapped = Rectangle.FromLTRB(
            (int)Math.Round((display.Left - offset.X) / scale),
            (int)Math.Round((display.Top - offset.Y) / scale),
            (int)Math.Round((display.Right - offset.X) / scale),
            (int)Math.Round((display.Bottom - offset.Y) / scale));
        return Rectangle.Intersect(mapped, new Rectangle(0, 0, _still.Width, _still.Height));
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

        var frameRect = ToFramePixels(_dragRect);
        if (frameRect.Width < 40 || frameRect.Height < 16)
        {
            // A stray click, not a chat tab. Keep the still up and let them drag.
            _dragRect = Rectangle.Empty;
            Invalidate();
            return;
        }

        Selection = frameRect;
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

        var (scale, offset) = Placement();
        // Nearest neighbour: at the usual 1:1 this is a straight blit, and any
        // scaled text stays blocky-sharp instead of OCR-hostile blurry.
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_still, new RectangleF(offset.X, offset.Y, _still.Width * scale, _still.Height * scale));

        // Snipping-tool grammar: everything dims except the current drag, so
        // the chosen rectangle reads as the part that stays.
        using (var dim = new SolidBrush(Color.FromArgb(96, 0, 0, 0)))
        {
            if (_dragRect.Width > 0) g.SetClip(_dragRect, CombineMode.Exclude);
            g.FillRectangle(dim, ClientRectangle);
            g.ResetClip();
        }

        const string hint = "This is a frozen frame of the game window — drag a rectangle around its loot chat tab. Esc cancels.";
        using var hintFont = new Font("Segoe UI", 14f, FontStyle.Bold);
        var hintSize = g.MeasureString(hint, hintFont);
        g.DrawString(hint, hintFont, Brushes.White,
            new PointF((ClientSize.Width - hintSize.Width) / 2f, ClientSize.Height * 0.12f));

        if (_dragRect.Width <= 0) return;
        using var pen = new Pen(Color.FromArgb(102, 217, 108), 2f);
        g.DrawRectangle(pen, _dragRect);
        var frameRect = ToFramePixels(_dragRect);
        using var sizeFont = new Font("Segoe UI", 10f);
        g.DrawString($"{frameRect.Width} × {frameRect.Height}", sizeFont, Brushes.White,
            _dragRect.X, Math.Max(0, _dragRect.Y - 22));
    }
}
