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
}
