using System.Diagnostics;

namespace LushbdoCompanion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray icon per machine; a second launch just exits quietly.
        using var mutex = new Mutex(true, "lushbdo-companion-single-instance", out var isFirst);
        if (!isFirst) return;

        // This lives beside a running game and must never compete with it for
        // CPU. Everything here is background work; OCR half a second late is
        // invisible, a dropped game frame is not.
        using (var self = Process.GetCurrentProcess())
            self.PriorityClass = ProcessPriorityClass.BelowNormal;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
