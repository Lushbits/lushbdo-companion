using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The unmanaged plumbing beneath Windows.Graphics.Capture: a D3D11 device for
/// the frame pool, the factory that turns an HMONITOR into a capture item, and
/// raw byte access to SoftwareBitmap pixels. Passive by construction — every
/// entry point here produces pixels, none of them sends input anywhere.
/// </summary>
internal static class CaptureInterop
{
    // --- A D3D11 device, wrapped for WinRT ----------------------------------

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, uint driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out IntPtr featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        const uint DriverTypeHardware = 1, DriverTypeWarp = 5;
        const uint BgraSupport = 0x20;
        const uint SdkVersion = 7;

        var hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, BgraSupport,
            IntPtr.Zero, 0, SdkVersion, out var d3dDevice, out _, out var context);
        if (hr < 0) // No usable GPU (remote desktop, odd drivers): WARP still captures fine at 2 fps.
            hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, BgraSupport,
                IntPtr.Zero, 0, SdkVersion, out d3dDevice, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        Marshal.Release(context);

        var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, ref iidDxgiDevice, out var dxgiDevice));
        Marshal.Release(d3dDevice);
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var abi));
            try { return MarshalInterface<IDirect3DDevice>.FromAbi(abi); }
            finally { Marshal.Release(abi); }
        }
        finally { Marshal.Release(dxgiDevice); }
    }

    // --- HMONITOR → GraphicsCaptureItem -------------------------------------

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        var abi = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(hmonitor, ref iid);
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    // --- Which monitor holds the picked region ------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetMonitorInfoW(IntPtr hmonitor, ref MONITORINFO info);

    /// <summary>The monitor nearest the region, with its bounds in physical pixels.</summary>
    public static (IntPtr Handle, Rectangle Bounds) MonitorFor(Rectangle region)
    {
        const uint MonitorDefaultToNearest = 2;
        var rect = new RECT { Left = region.Left, Top = region.Top, Right = region.Right, Bottom = region.Bottom };
        var handle = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(handle, ref info))
            throw new InvalidOperationException("the monitor's bounds could not be read.");
        return (handle, Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
    }

    // --- Raw pixels of a SoftwareBitmap -------------------------------------

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    // --- GPU-side crop -------------------------------------------------------
    // Copying a whole monitor to the CPU twice a second is the kind of cost a
    // gamer notices. These raw D3D11 calls copy just the chat-sized rectangle
    // into a staging texture and read only that back — the full frame never
    // crosses the bus. Vtable slots are stable ABI, same on every Windows.

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    public static readonly Guid ID3D11Device = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    public static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>The D3D11 object under a WinRT Direct3D wrapper (device or surface). Caller releases.</summary>
    public static IntPtr GetD3DPointer(object direct3DObject, Guid iid) =>
        direct3DObject.As<IDirect3DDxgiInterfaceAccess>().GetInterface(ref iid);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE { public IntPtr Data; public uint RowPitch, DepthPitch; }

    public static unsafe IntPtr CreateStagingTexture(IntPtr d3dDevice, int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = 87,             // DXGI_FORMAT_B8G8R8A8_UNORM, what the frame pool produces
            SampleCount = 1,
            Usage = 3,               // D3D11_USAGE_STAGING
            CPUAccessFlags = 0x20000 // D3D11_CPU_ACCESS_READ
        };
        IntPtr texture;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
            (*(void***)d3dDevice)[5])(d3dDevice, &desc, IntPtr.Zero, &texture); // ID3D11Device::CreateTexture2D
        Marshal.ThrowExceptionForHR(hr);
        return texture;
    }

    public static unsafe IntPtr GetImmediateContext(IntPtr d3dDevice)
    {
        IntPtr context;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)
            (*(void***)d3dDevice)[40])(d3dDevice, &context); // ID3D11Device::GetImmediateContext
        return context;
    }

    public static unsafe void CopyRegion(IntPtr context, IntPtr dstTexture, IntPtr srcTexture, Rectangle srcRect)
    {
        var box = new D3D11_BOX
        {
            Left = (uint)srcRect.Left,
            Top = (uint)srcRect.Top,
            Right = (uint)srcRect.Right,
            Bottom = (uint)srcRect.Bottom,
            Back = 1
        };
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)
            (*(void***)context)[46])(context, dstTexture, 0, 0, 0, 0, srcTexture, 0, &box); // ID3D11DeviceContext::CopySubresourceRegion
    }

    /// <summary>Reads the staging texture into tightly packed BGRA rows.</summary>
    public static unsafe void ReadTexture(IntPtr context, IntPtr stagingTexture, int width, int height, byte[] into)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)
            (*(void***)context)[14])(context, stagingTexture, 0, 1 /* D3D11_MAP_READ */, 0, &mapped); // ID3D11DeviceContext::Map
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var rowBytes = width * 4;
            fixed (byte* dst = into)
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy((byte*)mapped.Data + y * mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                (*(void***)context)[15])(context, stagingTexture, 0); // ID3D11DeviceContext::Unmap
        }
    }
}
