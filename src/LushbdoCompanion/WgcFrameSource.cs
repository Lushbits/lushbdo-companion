using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LushbdoCompanion;

/// <summary>
/// Windows.Graphics.Capture as an IFrameSource, built to cost nothing next to
/// a running game. The frame pool is drained on a timer, not on compositor
/// events: between ticks the pool sits full and the compositor skips us
/// entirely, so capture work is a couple of GPU copies per second instead of
/// one per rendered frame. The region is cropped on the GPU and only that
/// chat-sized rectangle is ever read back to the CPU, into a buffer allocated
/// once. Same passive path as OBS — the game is never touched.
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    public event Action<RegionFrame?>? Tick;
    public event Action<Exception>? Failed;

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private System.Threading.Timer? _timer;
    private SizeInt32 _poolSize;
    private Rectangle _region;              // requested crop, monitor-relative physical pixels
    private IntPtr _d3dDevice, _d3dContext, _staging;
    private Size _stagingSize;
    private byte[] _pixels = [];
    private int _busy;
    private volatile bool _disposed;

    public async Task StartAsync(IntPtr monitor, Rectangle regionOnMonitor, TimeSpan pace)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("this Windows build cannot do passive screen capture (Windows 10 2004 or newer is needed).");

        _region = regionOnMonitor;
        _device = CaptureInterop.CreateDirect3DDevice();
        _d3dDevice = CaptureInterop.GetD3DPointer(_device, CaptureInterop.ID3D11Device);
        _d3dContext = CaptureInterop.GetImmediateContext(_d3dDevice);

        var item = CaptureInterop.CreateItemForMonitor(monitor);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _pool.CreateCaptureSession(item);
        // The pointer crossing the chat must not corrupt a line mid-read.
        _session.IsCursorCaptureEnabled = false;
        await TryDisableBorderAsync(_session);
        _session.StartCapture();

        _timer = new System.Threading.Timer(OnTimer, null, pace, pace);
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

    private void OnTimer(object? state)
    {
        if (_disposed || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            // Everything queued since the last tick is stale except the newest;
            // freeing the buffers here is also what invites the compositor to
            // copy again, so its work stays bounded by our pace.
            Direct3D11CaptureFrame? frame = null;
            while (_pool!.TryGetNextFrame() is { } next)
            {
                frame?.Dispose();
                frame = next;
            }

            if (frame is null)
            {
                Tick?.Invoke(null); // alive, but the screen produced nothing new
                return;
            }

            using (frame)
            {
                var content = frame.ContentSize;
                var crop = Rectangle.Intersect(_region, new Rectangle(0, 0, content.Width, content.Height));
                if (crop.Width < 1 || crop.Height < 1)
                {
                    Tick?.Invoke(null); // the region fell off the monitor (mode change); keep ticking
                }
                else
                {
                    EnsureStaging(crop.Size);
                    var frameTexture = CaptureInterop.GetD3DPointer(frame.Surface, CaptureInterop.ID3D11Texture2D);
                    try { CaptureInterop.CopyRegion(_d3dContext, _staging, frameTexture, crop); }
                    finally { Marshal.Release(frameTexture); }
                    CaptureInterop.ReadTexture(_d3dContext, _staging, crop.Width, crop.Height, _pixels);
                    Tick?.Invoke(new RegionFrame(_pixels, crop.Width, crop.Height));
                }

                if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
                {
                    // The monitor changed mode under us; follow it after the
                    // frame is done being read, as the old surface dies with
                    // the recreate.
                    _poolSize = content;
                    _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                }
            }
        }
        catch (Exception e)
        {
            if (!_disposed) Failed?.Invoke(e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void EnsureStaging(Size size)
    {
        if (size == _stagingSize) return;
        if (_staging != IntPtr.Zero) Marshal.Release(_staging);
        _staging = CaptureInterop.CreateStagingTexture(_d3dDevice, size.Width, size.Height);
        _stagingSize = size;
        _pixels = new byte[size.Width * size.Height * 4];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait out an in-flight tick before pulling the D3D objects from under it.
        if (_timer is not null)
        {
            using var drained = new ManualResetEvent(false);
            if (_timer.Dispose(drained)) drained.WaitOne(2000);
        }

        _session?.Dispose();
        _pool?.Dispose();
        if (_staging != IntPtr.Zero) Marshal.Release(_staging);
        if (_d3dContext != IntPtr.Zero) Marshal.Release(_d3dContext);
        if (_d3dDevice != IntPtr.Zero) Marshal.Release(_d3dDevice);
        (_device as IDisposable)?.Dispose();
    }
}
