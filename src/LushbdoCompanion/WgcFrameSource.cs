using System.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace LushbdoCompanion;

/// <summary>
/// Windows.Graphics.Capture as an IFrameSource: the compositor hands us every
/// frame the monitor draws, we keep one per pace tick and copy it to a CPU
/// bitmap. This is the same capture path OBS uses — the OS knows about it,
/// indicates it, and the game is never touched.
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    public event Action<SoftwareBitmap>? FrameArrived;
    public event Action<Exception>? FrameFailed;

    private readonly Stopwatch _sinceLastFrame = Stopwatch.StartNew();
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private TimeSpan _pace;
    private int _busy;
    private volatile bool _disposed;

    public async Task StartAsync(IntPtr monitor, TimeSpan pace)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("this Windows build cannot do passive screen capture (Windows 10 2004 or newer is needed).");

        _pace = pace;
        _device = CaptureInterop.CreateDirect3DDevice();
        var item = CaptureInterop.CreateItemForMonitor(monitor);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnPoolFrame;
        _session = _pool.CreateCaptureSession(item);
        // The pointer crossing the chat must not corrupt a line mid-read.
        _session.IsCursorCaptureEnabled = false;
        await TryDisableBorderAsync(_session);
        _session.StartCapture();
    }

    /// <summary>
    /// Windows 11 22H2+ has an API to drop the yellow "this monitor is being
    /// captured" border, and grants it to desktop apps on request. On Windows
    /// 10 the API does not exist and the border is unavoidable — the README
    /// says so rather than pretending otherwise.
    /// </summary>
    private static async Task TryDisableBorderAsync(GraphicsCaptureSession session)
    {
#pragma warning disable CA1416 // guarded at runtime via ApiInformation, which the analyzer cannot follow
        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                return;
            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            session.IsBorderRequired = false;
        }
        catch
        {
            // Denied or an in-between build: the border stays. Cosmetic only.
        }
#pragma warning restore CA1416
    }

    private void OnPoolFrame(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        // The compositor offers a frame whenever anything on screen changes;
        // take one per pace tick, keep one in flight, drop the rest.
        if (_disposed || _sinceLastFrame.Elapsed < _pace || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            frame.Dispose();
            return;
        }
        _sinceLastFrame.Restart();
        _ = DeliverAsync(frame);
    }

    private async Task DeliverAsync(Direct3D11CaptureFrame frame)
    {
        try
        {
            SoftwareBitmap bitmap;
            using (frame)
            {
                if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
                {
                    // The monitor changed mode under us (resolution or scaling
                    // switch): follow it; the watcher re-clamps its crop.
                    _poolSize = frame.ContentSize;
                    _pool!.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                }
                bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);
            }

            if (_disposed || FrameArrived is not { } deliver)
            {
                bitmap.Dispose();
                return;
            }
            deliver(bitmap);
        }
        catch (Exception e)
        {
            if (!_disposed) FrameFailed?.Invoke(e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pool is not null) _pool.FrameArrived -= OnPoolFrame;
        _session?.Dispose();
        _pool?.Dispose();
        (_device as IDisposable)?.Dispose();
    }
}
