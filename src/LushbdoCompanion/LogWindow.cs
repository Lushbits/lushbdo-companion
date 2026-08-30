namespace LushbdoCompanion;

/// <summary>
/// The live log — every line the app reads, matches, holds or sends, as it
/// happens. This is also the debugging surface for when OCR misbehaves, which
/// is why it exists from the first milestone rather than arriving with polish.
/// </summary>
public sealed class LogWindow : Form
{
    private readonly TextBox _box;

    public LogWindow()
    {
        Text = "LushBDO Companion — log";
        if (AppIcon.Window() is { } icon) Icon = icon;
        // Sized in 96-DPI pixels; Dpi auto-scaling keeps the window sensible
        // at 125 %/150 % now that the app is per-monitor DPI aware.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Width = 720;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;

        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            BackColor = Color.FromArgb(16, 18, 20),
            ForeColor = Color.FromArgb(220, 224, 228)
        };
        Controls.Add(_box);
    }

    public void Append(string message)
    {
        if (IsDisposed) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (InvokeRequired) BeginInvoke(() => AppendCore(line));
        else AppendCore(line);
    }

    private void AppendCore(string line)
    {
        // Hours of play must not grow the box without bound: past ~1 MB of
        // text, drop the oldest half. The log is a live surface, not an archive.
        if (_box.TextLength > 1_000_000)
        {
            var text = _box.Text;
            var cut = text.IndexOf(Environment.NewLine, text.Length / 2, StringComparison.Ordinal);
            _box.Text = cut < 0 ? "" : text[(cut + Environment.NewLine.Length)..];
            _box.SelectionStart = _box.TextLength;
        }
        _box.AppendText(line);
    }

    /// <summary>Closing hides — the log keeps accumulating for the tray to reopen.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
