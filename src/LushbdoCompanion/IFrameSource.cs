using Windows.Graphics.Imaging;

namespace LushbdoCompanion;

/// <summary>
/// A source of whole-monitor frames for the watcher. Windows.Graphics.Capture
/// is the implementation today; this seam exists so a GDI BitBlt fallback
/// could replace it without the crop/OCR side noticing. Implementations are
/// passive by contract — pixels out, nothing in, same class as OBS.
/// </summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// One frame of the captured monitor. Sources pace themselves — this never
    /// fires faster than the interval given to StartAsync. The handler owns
    /// the bitmap and must dispose it.
    /// </summary>
    event Action<SoftwareBitmap>? FrameArrived;

    /// <summary>A frame that could not be produced; the source keeps running.</summary>
    event Action<Exception>? FrameFailed;

    /// <summary>Begin producing frames of the given monitor (an HMONITOR).</summary>
    Task StartAsync(IntPtr monitor, TimeSpan pace);
}
