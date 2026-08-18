using System.Drawing;

namespace LushbdoCompanion;

/// <summary>
/// One captured frame of the watched region: tightly packed 32-bit BGRA rows.
/// The pixel buffer is reused between frames — handlers copy what they need
/// before returning.
/// </summary>
public readonly record struct RegionFrame(byte[] Pixels, int Width, int Height);

/// <summary>
/// A source of region pixels for the watcher. Windows.Graphics.Capture is the
/// implementation today; this seam exists so a GDI BitBlt fallback could
/// replace it without the OCR side noticing. Implementations are passive by
/// contract — pixels out, nothing in, same class as OBS — and pace themselves:
/// a tick fires roughly per pace interval while the source is alive, carrying
/// a frame when the screen produced one and null when it did not. Silence
/// therefore always means the source died; the watcher's heartbeat rides on
/// that distinction.
/// </summary>
public interface IFrameSource : IDisposable
{
    event Action<RegionFrame?>? Tick;

    /// <summary>A tick that could not be produced; the source keeps running.</summary>
    event Action<Exception>? Failed;

    /// <summary>Begin watching a rectangle of the given monitor (an HMONITOR), in monitor-relative physical pixels.</summary>
    Task StartAsync(IntPtr monitor, Rectangle regionOnMonitor, TimeSpan pace);
}
