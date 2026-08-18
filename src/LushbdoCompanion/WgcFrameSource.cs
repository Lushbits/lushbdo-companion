using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LushbdoCompanion;

/// <summary>
/// Windows.Graphics.Capture over the game's own window, as an IFrameSource
/// built to cost nothing next to a running game. Capturing the window rather
/// than the monitor means the source can only ever see the game — other
/// windows crossing the region are structurally invisible — and keeps reading
/// it while it sits behind Chrome. The source owns the window's lifecycle: no
/// game means idle waiting, a closed game means quietly re-acquiring by
/// process, never an error loop. The frame pool is drained on a timer, not on
/// compositor events: between ticks the pool sits full and the compositor
/// skips us entirely, so capture work is a couple of GPU copies per second
/// instead of one per rendered frame. The region is cropped on the GPU and
/// only that chat-sized rectangle is ever read back to the CPU, into a buffer
/// allocated once. Same passive path as OBS — the game is never touched.
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    public event Action<RegionFrame?>? Tick;
    public event Action<Exception>? Failed;
    public event Action<string>? Status;

    // A process-list walk is not free; while the game is down, look for it
    // every Nth tick (~3 s at the 500 ms pace) instead of every tick.
    private const int TicksBetweenSearches = 6;

    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private System.Threading.Timer? _timer;
    private SizeInt32 _poolSize;
    private Rectangle _region;              // requested crop, window-relative physical pixels
    private IntPtr _d3dDevice, _d3dContext, _staging;
    private Size _stagingSize;
    private byte[] _pixels = [];
    private int _busy;
    private int _ticksUntilSearch;
    private volatile bool _windowClosed;    // flipped by the capture item's Closed event, acted on at the next tick
    private volatile bool _disposed;

    public async Task StartAsync(Rectangle regionInWindow, TimeSpan pace)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("this Windows build cannot do passive screen capture (Windows 10 2004 or newer is needed).");

        _region = regionInWindow;
        _device = CaptureInterop.CreateDirect3DDevice();
        _d3dDevice = CaptureInterop.GetD3DPointer(_device, CaptureInterop.ID3D11Device);
        _d3dContext = CaptureInterop.GetImmediateContext(_d3dDevice);
        await EnsureBorderlessAccessAsync();

        if (!TryAcquireWindow())
            Status?.Invoke($"Waiting for the game window ({GameWindow.Description}) — watching starts by itself once the game is up.");

        _timer = new System.Threading.Timer(OnTimer, null, pace, pace);
    }

    /// <summary>
    /// Photographs one frame of a window for the region picker — the same
    /// passive compositor read as the continuous path, done once and torn
    /// down. Works with the window buried under others; that is the point.
    /// </summary>
    public static async Task<Bitmap> CaptureStillAsync(IntPtr window)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("this Windows build cannot do passive screen capture (Windows 10 2004 or newer is needed)");

        var device = CaptureInterop.CreateDirect3DDevice();
        var d3dDevice = CaptureInterop.GetD3DPointer(device, CaptureInterop.ID3D11Device);
        var d3dContext = CaptureInterop.GetImmediateContext(d3dDevice);
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        var staging = IntPtr.Zero;
        try
        {
            var item = CaptureInterop.CreateItemForWindow(window);
            var poolSize = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            await EnsureBorderlessAccessAsync();
            TryDisableBorder(session);

            var first = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (p, _) =>
            {
                if (p.TryGetNextFrame() is { } f && !first.TrySetResult(f))
                    f.Dispose();
            };
            session.StartCapture();

            Direct3D11CaptureFrame frame;
            try { frame = await first.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException)
            {
                // A minimized window is not composited, so it has no frame to serve.
                throw new InvalidOperationException("it produced no image — if the game is minimized, restore it and try again");
            }

            using (frame)
            {
                var content = frame.ContentSize;
                var crop = Rectangle.Intersect(
                    new Rectangle(0, 0, content.Width, content.Height),
                    new Rectangle(0, 0, poolSize.Width, poolSize.Height));
                staging = CaptureInterop.CreateStagingTexture(d3dDevice, crop.Width, crop.Height);
                var frameTexture = CaptureInterop.GetD3DPointer(frame.Surface, CaptureInterop.ID3D11Texture2D);
                try { CaptureInterop.CopyRegion(d3dContext, staging, frameTexture, crop); }
                finally { Marshal.Release(frameTexture); }
                var pixels = new byte[crop.Width * crop.Height * 4];
                CaptureInterop.ReadTexture(d3dContext, staging, crop.Width, crop.Height, pixels);
                return ToBitmap(pixels, crop.Width, crop.Height);
            }
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
            if (staging != IntPtr.Zero) Marshal.Release(staging);
            Marshal.Release(d3dContext);
            Marshal.Release(d3dDevice);
            (device as IDisposable)?.Dispose();
        }
    }

    private static Bitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private bool TryAcquireWindow()
    {
        if (GameWindow.Find() is not { } game) return false;
        try
        {
            var item = CaptureInterop.CreateItemForWindow(game.Hwnd);
            item.Closed += (_, _) => _windowClosed = true;
            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _session = _pool.CreateCaptureSession(item);
            // The pointer crossing the chat must not corrupt a line mid-read.
            _session.IsCursorCaptureEnabled = false;
            TryDisableBorder(_session);
            _session.StartCapture();
            Status?.Invoke($"Game window found ({_poolSize.Width}×{_poolSize.Height}) — watching it, covered or not.");
            return true;
        }
        catch
        {
            // The window can die between finding it and capturing it (a game
            // mid-exit). Clean up and let the next search try again.
            DropCapture();
            return false;
        }
    }

    private void DropCapture()
    {
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        _windowClosed = false;
    }

    private void OnTimer(object? state)
    {
        if (_disposed || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (_windowClosed)
            {
                // The game exited. The saved region is window-relative, so it
                // survives untouched — just wait for the process to come back.
                DropCapture();
                _ticksUntilSearch = 0;
                Status?.Invoke("The game window closed — waiting for it to come back.");
            }

            if (_session is null)
            {
                if (--_ticksUntilSearch <= 0)
                {
                    _ticksUntilSearch = TicksBetweenSearches;
                    TryAcquireWindow();
                }
                Tick?.Invoke(null); // idling, but alive — the heartbeat rides on this
                return;
            }

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
                Tick?.Invoke(null); // alive, but the window produced nothing new
                return;
            }

            using (frame)
            {
                var content = frame.ContentSize;
                var crop = Rectangle.Intersect(_region, new Rectangle(0, 0, content.Width, content.Height));
                if (crop.Width < 1 || crop.Height < 1)
                {
                    Tick?.Invoke(null); // the region fell outside the window (resolution change); keep ticking
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
                    // The window was resized under us; follow it after the
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

    // --- The yellow capture border ------------------------------------------
    // Windows 11 22H2+ has an API to drop the "this window is being captured"
    // border, and grants it to desktop apps on request. On Windows 10 the API
    // does not exist and the border is unavoidable — the README says so rather
    // than pretending otherwise. The access request runs once per process; the
    // per-session opt-out is synchronous so re-acquiring the game window from
    // the capture timer can use it too.

    private static bool _borderlessRequested;

#pragma warning disable CA1416 // guarded at runtime via ApiInformation, which the analyzer cannot follow
    private static async Task EnsureBorderlessAccessAsync()
    {
        if (_borderlessRequested) return;
        _borderlessRequested = true;
        try
        {
            if (ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch
        {
            // Denied or an in-between build: the border stays. Cosmetic only.
        }
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch
        {
            // Same story: cosmetic only.
        }
    }
#pragma warning restore CA1416

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
