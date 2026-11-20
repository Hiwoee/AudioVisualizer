using System.Diagnostics;
using Microsoft.Win32;

namespace AudioVisualizer;

public sealed class OverlayApp : ApplicationContext
{
    private const string AppName      = "Audio Visualizer";
    private const string RunKeyPath   = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AudioVisualizer";

    private static readonly (string Name, string Key, bool OnByDefault, Func<AppSettings, OverlayWindow> Create)[] Overlays =
    [
        ("Audio visualizer", "visualizer", true,  s => new VisualizerOverlay(s)),
        ("Test image",       "testimage",  false, s => new TestImageOverlay(s)),
    ];

    private readonly OverlayWindow?[]     _open         = new OverlayWindow?[Overlays.Length];
    private readonly ToolStripMenuItem[]  _overlayItems = new ToolStripMenuItem[Overlays.Length];

    private readonly string                     _settingsPath;
    private readonly SynchronizationContext     _ui = new WindowsFormsSynchronizationContext();
    private readonly NotifyIcon                 _trayIcon;
    private readonly FileSystemWatcher          _settingsWatcher;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 250 };

    private bool _stayOnTop;
    private bool _exiting;

    public OverlayApp(AppSettings settings, string settingsPath)
    {
        _settingsPath = settingsPath;
        _trayIcon     = CreateTrayIcon();

        _settingsWatcher = new FileSystemWatcher(
            Path.GetDirectoryName(_settingsPath)!,
            Path.GetFileName(_settingsPath))
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _settingsWatcher.Changed += OnSettingsFileChanged;
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadSettings(); };

        SystemEvents.DisplaySettingsChanged += OnScreenChanged;
        SystemEvents.UserPreferenceChanged  += OnUserPreferenceChanged;

        Apply(settings);
    }

    private void Apply(AppSettings settings)
    {
        _stayOnTop = settings.Stayontop;

        for (int i = 0; i < Overlays.Length; i++)
        {
            var  overlay = _open[i];
            bool on      = settings.OverlayOn(Overlays[i].Key, Overlays[i].OnByDefault);
            _overlayItems[i].Checked = on;

            if (!on)
            {
                overlay?.Close();
                _open[i] = null;
                continue;
            }

            if (overlay is null)
            {
                overlay = _open[i] = Overlays[i].Create(settings);
                overlay.SetStayOnTop(_stayOnTop);
                overlay.Show();
                continue;
            }

            overlay.ApplySettings(settings);
            overlay.SetStayOnTop(_stayOnTop);
        }
    }

    private NotifyIcon CreateTrayIcon()
    {
        using Icon? extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var trayIcon = new NotifyIcon
        {
            Icon    = extracted is null ? SystemIcons.Application : (Icon)extracted.Clone(),
            Visible = true,
            Text    = AppName
        };

        var menu = new ContextMenuStrip();
        for (int i = 0; i < Overlays.Length; i++)
        {
            int index = i;
            _overlayItems[i] = new ToolStripMenuItem(Overlays[i].Name);
            _overlayItems[i].Click += (_, _) => ToggleOverlay(index);
            menu.Items.Add(_overlayItems[i]);
        }
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open settings",      null, (_, _) => OpenSettingsFile());
        menu.Items.Add("Reload settings",    null, (_, _) => ReloadSettings());
        menu.Items.Add("Toggle stay on top", null, (_, _) => ToggleStayOnTop());

        var startWithWindows = new ToolStripMenuItem("Start with Windows") { Checked = IsStartWithWindowsOn() };
        startWithWindows.Click += (_, _) => ToggleStartWithWindows(startWithWindows);
        menu.Items.Add(startWithWindows);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ToggleStayOnTop();
        return trayIcon;
    }

    private void Warn(string message)
        => _trayIcon.ShowBalloonTip(3000, AppName, message, ToolTipIcon.Warning);

    private void OpenSettingsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settingsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"Couldn't open settings.ini:\n{ex.Message}");
        }
    }

    private static string StartupCommand => $"\"{Environment.ProcessPath}\"";

    private static bool IsStartWithWindowsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return string.Equals(key?.GetValue(RunValueName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    private void ToggleStartWithWindows(ToolStripMenuItem item)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (IsStartWithWindowsOn()) key.DeleteValue(RunValueName, false);
            else                        key.SetValue(RunValueName, StartupCommand);
        }
        catch (Exception ex)
        {
            Warn($"Couldn't change Start with Windows:\n{ex.Message}");
        }
        item.Checked = IsStartWithWindowsOn();
    }

    private void ToggleOverlay(int index)
    {
        try
        {
            Ini.SetValue(_settingsPath, "overlays", Overlays[index].Key, _overlayItems[index].Checked ? "false" : "true");
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }
        catch (Exception ex)
        {
            Warn($"Couldn't change settings.ini:\n{ex.Message}");
        }
    }

    private void OnScreenChanged(object? sender, EventArgs e)
        => _ui.Post(_ =>
        {
            if (_exiting) return;
            foreach (var overlay in _open) overlay?.Reposition();
        }, null);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop) OnScreenChanged(sender, e);
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_exiting) return;
        _ui.Post(_ =>
        {
            if (_exiting) return;
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }, null);
    }

    private void ReloadSettings()
    {
        if (_exiting) return;
        try
        {
            Apply(new AppSettings(_settingsPath));
        }
        catch (Exception ex)
        {
            Warn($"Couldn't reload settings.ini:\n{ex.Message}");
        }
    }

    private void ToggleStayOnTop()
    {
        _stayOnTop = !_stayOnTop;
        foreach (var overlay in _open) overlay?.SetStayOnTop(_stayOnTop);
    }

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;

        SystemEvents.DisplaySettingsChanged -= OnScreenChanged;
        SystemEvents.UserPreferenceChanged  -= OnUserPreferenceChanged;
        _reloadDebounce.Stop();
        _reloadDebounce.Dispose();
        _settingsWatcher.EnableRaisingEvents = false;
        _settingsWatcher.Dispose();

        foreach (var overlay in _open) overlay?.Close();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        ExitThread();
    }
}
