namespace LushbdoCompanion;

/// <summary>
/// The three-second heads-up before the live-overlay fallback picker opens:
/// the game window was not found, so the pick happens over the live screen and
/// the game needs to be brought in front first. Deliberately never takes focus
/// — the whole point is that the user is clicking into the game while this
/// counts down.
/// </summary>
public sealed class CountdownForm : Form
{
    private readonly Label _label;

    public CountdownForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(16, 18, 20);

        _label = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            Padding = new Padding(28, 18, 28, 18)
        };
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => true;

    public void SetText(string text)
    {
        _label.Text = text;
        ClientSize = _label.PreferredSize;
        var screen = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        Location = new Point(screen.X + (screen.Width - Width) / 2, screen.Y + screen.Height / 8);
    }
}
