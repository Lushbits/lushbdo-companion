using System.Reflection;

namespace LushbdoCompanion;

/// <summary>
/// The site's own L mark, so the thing in the tray looks like the thing it
/// feeds. `lushbdo.ico` is built from the site repo's `static/logo-mark.png`,
/// not from its `favicon.ico`: the favicon's corners came through the tray
/// white, because a rounded tile with transparent corners has to put
/// *something* in them once it is flattened into an icon's masks.
///
/// So the mark is cropped past its own rounding first — by the smallest inset
/// at which every pixel is fully opaque, measured rather than picked, which
/// came to 4px of 128 — and the result is a square tile with nothing to render
/// wrong. It carries 16/20/24/32/40/48/64 as 32-bit bitmaps and 128/256 as
/// PNG, so Windows picks per size and per DPI rather than scaling one bitmap
/// badly, and the big two do not triple the file.
///
/// It rides as an embedded resource for the same reason the OCR models do — the
/// release is one .exe, and a loose icon file beside the download would be an
/// install. The csproj also points `ApplicationIcon` at it, which is a separate
/// thing: that is the icon Explorer, the taskbar and the SmartScreen dialog
/// show for the file, and it cannot be loaded from a resource.
///
/// Every load is a fresh handle the caller owns. Null means the resource could
/// not be read at all, which is not worth failing a tray app over — the caller
/// falls back to a system icon and the app runs looking wrong rather than not
/// running.
/// </summary>
public static class AppIcon
{
    private const string Resource = "LushbdoCompanion.lushbdo.ico";

    /// <summary>The tray's size, which is DPI-dependent — the app is per-monitor aware, so ask rather than assume 16.</summary>
    public static Icon? Tray() => Load(SystemInformation.SmallIconSize);

    /// <summary>A window's title bar and its taskbar button.</summary>
    public static Icon? Window() => Load(SystemInformation.IconSize);

    private static Icon? Load(Size size)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
            if (stream is null) return null;
            // The (stream, size) overload picks the closest image in the file
            // rather than taking the first and resampling it.
            return new Icon(stream, size);
        }
        catch
        {
            return null;
        }
    }
}
