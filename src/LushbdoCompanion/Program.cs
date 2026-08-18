namespace LushbdoCompanion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray icon per machine; a second launch just exits quietly.
        using var mutex = new Mutex(true, "lushbdo-companion-single-instance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
