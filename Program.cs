namespace AudioVisualizer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetColorMode(SystemColorMode.System);

        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        EnsureDefaultSettingsFile(settingsPath);

        AppSettings settings;
        try
        {
            settings = new AppSettings(settingsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load settings:\n{ex.Message}",
                "Audio Visualizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new OverlayApp(settings, settingsPath));
    }

    private static void EnsureDefaultSettingsFile(string path)
    {
        if (File.Exists(path)) return;
        File.WriteAllLines(path, new[]
        {
            "[overlays]",
            "visualizer=true",
            "testimage=false",
            "",
            "[config]",
            "sensitivity=75",
            "fps=90",
            "bars=250",
            "width=650",
            "height=150",
            "barspacing=0",
            "showpeakbars=true",
            "stayontop=true",
            "position=bottom-right",
            "monitor=0",
            "",
            "[mediainfo]",
            "mediainfo=true",
            "showtitle=true",
            "showsourceicon=true",
            "showprogressbar=true",
            "",
            "[color]",
            "r=30",
            "g=144",
            "b=155",
            "a=210"
        });
    }
}
