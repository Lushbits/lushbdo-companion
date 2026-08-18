using System.Drawing;

namespace LushbdoCompanion;

/// <summary>
/// One captured frame of the watched region: tightly packed 32-bit BGRA rows.
/// The pixel buffer is reused between frames — handlers copy what they need
/// before returning.
/// </summary>
public readonly record struct RegionFrame(byte[] Pixels, int Width, int Height);

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
    event Action<RegionFrame?>? Tick;

    /// <summary>A tick that could not be produced; the source keeps running.</summary>
    event Action<Exception>? Failed;

    /// <summary>Capture-state changes in words: waiting for the game window, found it, lost it.</summary>
    event Action<string>? Status;

    /// <summary>Begin watching a rectangle of the game window, in window-relative physical pixels.</summary>
    Task StartAsync(Rectangle regionInWindow, TimeSpan pace);
}
