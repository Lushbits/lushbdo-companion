using System.Drawing;

namespace LushbdoCompanion;

/// <summary>
/// One captured frame of the watched region: tightly packed 32-bit BGRA rows.
/// The pixel buffer is reused between frames — handlers copy what they need
/// before returning.
/// </summary>
public readonly record struct RegionFrame(byte[] Pixels, int Width, int Height);

/// <summary>
/// One tick's crops, in the order the rectangles were given to
/// <see cref="IFrameSource.StartAsync"/>. A slot is null when its rectangle
/// fell outside the window this tick (a resolution change).
///
/// Several rectangles cut out of the *same* compositor frame, rather than a
/// capture session each (#22): a second session on the same window doubles the
/// compositor's work per tick, and that is the featherweight rule in
/// CLAUDE.md, not a preference. Off a frame already in hand, another rectangle
/// is one more GPU copy and one more small readback.
///
/// The array is reused between ticks along with the pixel buffers inside it —
/// handlers copy what they need before returning.
/// </summary>
public readonly record struct FrameSet(RegionFrame?[] Regions)
{
    public RegionFrame? this[int index] => Regions[index];
}

/// <summary>
/// A source of region pixels for the watcher, cut from the game window's own
/// surface — never the desktop, so nothing but the game is ever readable.
/// Windows.Graphics.Capture is the implementation today; this seam exists so
/// another capture path could replace it without the OCR side noticing.
/// Implementations are passive by contract — pixels out, nothing in, same
/// class as OBS — and own finding (and re-finding) the game window: the game
/// not running means waiting, not failing. They pace themselves: a tick fires
/// roughly per pace interval while the source is alive, carrying a frame when
/// the window produced one and null when it did not (including the whole time
/// spent waiting for the game). Silence therefore always means the source
/// died; the watcher's heartbeat rides on that distinction.
/// </summary>
public interface IFrameSource : IDisposable
{
    event Action<FrameSet?>? Tick;

    /// <summary>A tick that could not be produced; the source keeps running.</summary>
    event Action<Exception>? Failed;

    /// <summary>Capture-state changes in words: waiting for the game window, found it, lost it.</summary>
    event Action<string>? Status;

    /// <summary>
    /// Begin watching one or more rectangles of the game window, in
    /// window-relative physical pixels. Every tick carries them all, in this
    /// order, cut from one frame.
    /// </summary>
    Task StartAsync(IReadOnlyList<Rectangle> regionsInWindow, TimeSpan pace);
}
