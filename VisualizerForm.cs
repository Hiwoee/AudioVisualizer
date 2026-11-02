using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AudioVisualizer;

public sealed class VisualizerForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndBottom  = new(1);

    private const uint SwpNoSize       = 0x0001;
    private const uint SwpNoMove       = 0x0002;
    private const uint SwpNoActivate   = 0x0010;
    private const int  WsExLayered     = 0x00080000;
    private const int  WsExToolWindow  = 0x00000080;
    private const int  WsExTransparent = 0x00000020;
    private const uint LwaColorKey     = 0x00000001;

    private const uint TimerHighResolution = 0x00000002;
    private const uint TimerAllAccess      = 0x001F0003;
    private const uint WaitInfinite        = 0xFFFFFFFF;

    private const string RunKeyPath   = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AudioVisualizer";

    private const int  FftSize           = 4096;
    private const long HideAfterPausedMs = 5000;

    private readonly float[]   _fftBuffer  = new float[FftSize];
    private readonly Complex[] _fftComplex = new Complex[FftSize];
    private readonly float[]   _magnitudes = new float[FftSize / 2];
    private          int       _fftWritePos;
    private          long      _lastAudioTick;

    private static readonly float[] HannWindow = BuildHannWindow();

    private static float[] BuildHannWindow()
    {
        var w = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            w[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
        return w;
    }

    private const int SourceIconSize = 16;

    private static readonly Image? SpotifyIcon = LoadIcon("Spotify.png");

    private static Image? LoadIcon(string file)
    {
        using var stream = typeof(VisualizerForm).Assembly.GetManifestResourceStream($"AudioVisualizer.icons.{file}");
        if (stream is null) return null;

        using var source = Image.FromStream(stream);
        var icon = new Bitmap(SourceIconSize, SourceIconSize);
        using var g = Graphics.FromImage(icon);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, SourceIconSize, SourceIconSize);
        return icon;
    }

    private float[] _smoothedLevels;
    private float[] _peakLevels;
    private float[] _rawTargets;

    private float _dynamicCeiling = 0.01f;

    private float  _marqueeOffset;
    private float  _marqueeWait;
    private string _lastMarqueeText = "";
    private bool   _marqueeActive;
    private float  _marqueeFullWidth;

    private const float MarqueeSpeedPxPerFrame = 0.5f;
    private const float MarqueeInitialWait     = 90f;
    private const float MarqueeLoopGap         = 60f;

    private TimeSpan _positionAnchor  = TimeSpan.Zero;
    private TimeSpan _mediaDuration   = TimeSpan.Zero;
    private DateTime _anchorSetAt     = DateTime.MinValue;
    private const double SeekThresholdSeconds = 2.5;

    private readonly string _settingsPath;

    private VisualizerSettings _settings;
    private bool               _stayOnTop;
    private SolidBrush         _barBrush;

    private WasapiLoopbackCapture?       _capture;
    private readonly MMDeviceEnumerator  _deviceEnumerator = new();
    private readonly DefaultDeviceWatcher _deviceWatcher;

    private NotifyIcon?                         _trayIcon;
    private readonly FileSystemWatcher          _settingsWatcher;
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _mediaTimer;
    private long                                _frameInterval;
    private int                                 _framePending;

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private bool _isRefreshingMedia;
    private bool _isPlaying;
    private long _lastPlayingTick;

    private readonly Font  _mediaFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Brush _peakBrush = Brushes.White;
    private readonly Brush _textBrush = Brushes.White;

    private MediaSnapshot _media = MediaSnapshot.Empty;
    private bool          _isDisposed;

    public VisualizerForm(VisualizerSettings settings, string settingsPath)
    {
        _settings     = settings;
        _settingsPath = settingsPath;
        _stayOnTop    = _settings.Stayontop;
        _barBrush     = new SolidBrush(_settings.BarColor);

        DoubleBuffered = true;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint  |
            ControlStyles.UserPaint             |
            ControlStyles.SupportsTransparentBackColor,
            true);
        UpdateStyles();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        BackColor       = Color.Black;
        TransparencyKey = BackColor;

        ApplyWindowBounds();
        ConfigureTrayIcon();

        int n           = _settings.BarCount;
        _smoothedLevels = new float[n];
        _peakLevels     = new float[n];
        _rawTargets     = new float[n];

        HandleCreated += OnHandleCreated;

        _settingsWatcher = new FileSystemWatcher(
            Path.GetDirectoryName(_settingsPath)!,
            Path.GetFileName(_settingsPath))
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _settingsWatcher.Changed += OnSettingsFileChanged;
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadSettings(); };

        _deviceWatcher = new DefaultDeviceWatcher(() =>
        {
            if (!_isDisposed && IsHandleCreated) BeginInvoke(new Action(RestartAudio));
        });
        _deviceEnumerator.RegisterEndpointNotificationCallback(_deviceWatcher);

        SetupAudio();
        SetupRenderTimer();
        new Thread(RenderLoop) { IsBackground = true, Name = "Render" }.Start();
        ApplyStayOnTop();

        _mediaTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
        _mediaTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExTransparent;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.Clear(BackColor);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_capture is null) return;

        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.HighSpeed;
        g.PixelOffsetMode   = PixelOffsetMode.HighSpeed;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var settings = _settings;

        int   barCount     = Math.Max(1, settings.BarCount);
        float totalSpacing = Math.Max(0, (barCount - 1) * settings.Barspacing);
        float barWidth     = Math.Max(1f, (Width - totalSpacing) / barCount);
        float stride       = barWidth + settings.Barspacing;

        for (int i = 0; i < barCount; i++)
        {
            int   x         = (int)(i * stride);
            int   w         = Math.Max(1, (int)((i + 1) * stride - settings.Barspacing) - x);
            float barHeight = Math.Min(_smoothedLevels[i], 1f) * Height;
            float y         = Height - barHeight;

            g.FillRectangle(_barBrush, x, y, w, barHeight);

            if (settings.Showpeakbars)
            {
                float peakY = Math.Max(0, Height - _peakLevels[i] - 2);
                g.FillRectangle(_peakBrush, x, peakY, w, 2);
            }
        }

        if (Environment.TickCount64 - _lastPlayingTick > HideAfterPausedMs) return;
        DrawMediaInfo(g, settings);
        DrawProgressBar(g, settings);
    }

    private static float HzToMel(float hz) => 2595f * MathF.Log10(1f + hz / 700f);
    private static float MelToHz(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);

    private void ProcessSpectrum(VisualizerSettings settings)
    {
        RunFFT();

        int   barCount   = Math.Max(1, settings.BarCount);
        float sampleRate = _capture?.WaveFormat.SampleRate ?? 44100f;
        float nyquist = sampleRate / 2f;
        float displayMaxHz = Math.Min(12500f, nyquist);
        float melMin = HzToMel(20f);
        float melMax = HzToMel(displayMaxHz);
        float tDivisor   = Math.Max(1, barCount - 1);

        for (int i = 0; i < barCount; i++)
        {
            float melLow   = melMin + (melMax - melMin) * i       / barCount;
            float melHigh  = melMin + (melMax - melMin) * (i + 1) / barCount;
            float freqLow  = MelToHz(melLow);
            float freqHigh = MelToHz(melHigh);

            int binLow  = Math.Clamp((int)(freqLow  / nyquist * (FftSize / 2)), 0, FftSize / 2 - 1);
            int binHigh = Math.Clamp((int)(freqHigh / nyquist * (FftSize / 2)), binLow + 1, FftSize / 2);

            float energy = 0f;
            for (int b = binLow; b < binHigh; b++)
                energy = Math.Max(energy, _magnitudes[b]);

            float db         = energy > 0f ? 20f * MathF.Log10(energy) : -120f;
            float normalized = Math.Clamp((db + 65f) / 65f, 0f, 1f);
            float final      = MathF.Pow(normalized, 0.6f);

            float t         = (float)i / tDivisor;
            float lowBoost  = 1.15f;
            float midShape  = 0.95f + 0.15f * MathF.Sin(t * MathF.PI);
            float highBoost = 1.0f + t * 1.4f;

            float gain = lowBoost * midShape * highBoost;

            final *= gain;
            final *= 0.97f + 0.03f * MathF.Sin(i * 0.2f);

            _rawTargets[i] = final;
        }

        ApplyGaussianSmooth(barCount, radius: 1, sigma: 0.8f);
        ApplyDynamicScale(settings, barCount);
    }

    private void RunFFT()
    {
        int start = _fftWritePos;
        for (int i = 0; i < FftSize; i++)
        {
            _fftComplex[i].X = _fftBuffer[(start + i) % FftSize] * HannWindow[i];
            _fftComplex[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), _fftComplex);

        for (int i = 0; i < FftSize / 2; i++)
        {
            float re = _fftComplex[i].X, im = _fftComplex[i].Y;
            _magnitudes[i] = MathF.Sqrt(re * re + im * im);
        }
    }

    private void ApplyGaussianSmooth(int barCount, int radius, float sigma)
    {
        Span<float> tmp = barCount <= 2048
            ? stackalloc float[barCount]
            : new float[barCount];

        for (int i = 0; i < barCount; i++)
        {
            float sum = 0f, weight = 0f;
            for (int j = -radius; j <= radius; j++)
            {
                int ni = i + j;
                if ((uint)ni >= (uint)barCount) continue;
                float w = MathF.Exp(-0.5f * j * j / (sigma * sigma));
                sum    += _rawTargets[ni] * w;
                weight += w;
            }
            tmp[i] = sum / weight;
        }

        tmp.CopyTo(_rawTargets.AsSpan(0, barCount));
    }

    private void ApplyDynamicScale(VisualizerSettings settings, int barCount)
    {
        float frameMax = 0f;
        for (int i = 0; i < barCount; i++)
            frameMax = Math.Max(frameMax, _rawTargets[i]);

        _dynamicCeiling = frameMax > _dynamicCeiling
            ? Lerp(_dynamicCeiling, frameMax, 0.18f)
            : Lerp(_dynamicCeiling, frameMax, 0.04f);
        _dynamicCeiling = Math.Max(_dynamicCeiling, 0.01f);

        float scale = (settings.Sensitivity / 100f) / _dynamicCeiling;

        for (int i = 0; i < barCount; i++)
        {
            float target = Math.Clamp(_rawTargets[i] * scale, 0f, 1f);

            _smoothedLevels[i] = _smoothedLevels[i] < target
                ? Lerp(_smoothedLevels[i], target, 0.8f)
                : Lerp(_smoothedLevels[i], target, 0.06f);

            float barHeight = _smoothedLevels[i] * Height;

            if (barHeight > _peakLevels[i])
                _peakLevels[i] = barHeight;
            else
                _peakLevels[i] = Math.Max(0f, _peakLevels[i] * 0.96f);
        }
    }

    private void UpdateMarquee()
    {
        if (!_marqueeActive) return;

        if (_marqueeWait < MarqueeInitialWait) { _marqueeWait++; return; }

        _marqueeOffset += MarqueeSpeedPxPerFrame;

        if (_marqueeOffset >= _marqueeFullWidth + MarqueeLoopGap)
        {
            _marqueeOffset = 0f;
            _marqueeWait   = 0f;
        }
    }

    private void DrawMediaInfo(Graphics g, VisualizerSettings settings)
    {
        if (!settings.Mediainfo || !settings.Showtitle) return;

        var media = _media;
        if (string.IsNullOrWhiteSpace(media.Title)) return;

        const int margin  = 10;
        const int artSize = 48;
        int textX = margin;
        int textY = Height - 60;

        if (media.AlbumArt is not null)
        {
            g.DrawImage(media.AlbumArt, new Rectangle(textX, Height - 80, artSize, artSize));
            textX += artSize + 10;
        }

        if (settings.Showsourceicon && SpotifyIcon is not null &&
            media.SourceApp?.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true)
        {
            g.DrawImage(SpotifyIcon, textX, textY + (_mediaFont.Height - SourceIconSize) / 2, SourceIconSize, SourceIconSize);
            textX += SourceIconSize + 6;
        }

        string text          = string.IsNullOrWhiteSpace(media.Artist)
            ? media.Title!
            : $"{media.Title} - {media.Artist}";
        int   availableWidth = Math.Max(50, Width - textX - margin);
        float textWidth      = g.MeasureString(text, _mediaFont).Width;

        if (textWidth <= availableWidth)
        {
            if (_lastMarqueeText != text) _lastMarqueeText = text;
            _marqueeActive = false;
            _marqueeOffset = 0f;
            _marqueeWait   = 0f;
            g.DrawString(text, _mediaFont, _textBrush, textX, textY);
        }
        else
        {
            if (text != _lastMarqueeText)
            {
                _lastMarqueeText = text;
                _marqueeOffset   = 0f;
                _marqueeWait     = 0f;
            }

            _marqueeActive    = true;
            _marqueeFullWidth = textWidth;

            var savedClip = g.Clip;
            g.SetClip(new RectangleF(textX, textY - 2, availableWidth, _mediaFont.Height + 4));
            g.DrawString(text, _mediaFont, _textBrush, textX - _marqueeOffset, textY);
            g.Clip = savedClip;
        }
    }

    private void DrawProgressBar(Graphics g, VisualizerSettings settings)
    {
        if (!settings.Showprogressbar) return;

        if (_mediaDuration.TotalSeconds <= 0 || _anchorSetAt == DateTime.MinValue) return;

        TimeSpan current  = _isPlaying ? _positionAnchor + (DateTime.Now - _anchorSetAt) : _positionAnchor;
        double   fraction = Math.Clamp(current.TotalSeconds / _mediaDuration.TotalSeconds, 0.0, 1.0);

        int barW = (int)(Width * fraction);
        if (barW <= 0) return;

        g.FillRectangle(Brushes.White, 0, Height - 3, barW, 3);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        DisposeResources();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeResources();
        base.Dispose(disposing);
    }

    private void ConfigureTrayIcon()
    {
        using Icon? extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _trayIcon = new NotifyIcon
        {
            Icon    = extracted is null ? SystemIcons.Application : (Icon)extracted.Clone(),
            Visible = true,
            Text    = "Audio Visualizer"
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings",      null, (_, _) => OpenSettingsFile());
        menu.Items.Add("Reload settings",    null, (_, _) => ReloadSettings());
        menu.Items.Add("Toggle stay on top", null, (_, _) => ToggleStayOnTop());

        var startWithWindows = new ToolStripMenuItem("Start with Windows") { Checked = IsStartWithWindowsOn() };
        startWithWindows.Click += (_, _) => ToggleStartWithWindows(startWithWindows);
        menu.Items.Add(startWithWindows);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ToggleStayOnTop();
    }

    private void OpenSettingsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_settingsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _trayIcon?.ShowBalloonTip(3000, "Audio Visualizer",
                $"Couldn't open settings.ini:\n{ex.Message}", ToolTipIcon.Warning);
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
            _trayIcon?.ShowBalloonTip(3000, "Audio Visualizer",
                $"Couldn't change Start with Windows:\n{ex.Message}", ToolTipIcon.Warning);
        }
        item.Checked = IsStartWithWindowsOn();
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(BackColor), 0, LwaColorKey);
        ApplyStayOnTop();
    }

    private void ApplyWindowBounds()
    {
        var area   = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        int width  = Math.Max(100, _settings.Width);
        int height = Math.Max(50,  _settings.Height);
        Size = new Size(width, height);

        Location = _settings.Position == "bottom-left"
            ? new Point(area.Left,          area.Bottom - height)
            : new Point(area.Right - width, area.Bottom - height);
    }

    private void SetupAudio()
    {
        WasapiLoopbackCapture? capture = null;
        try
        {
            capture = new WasapiLoopbackCapture();
            int channels = capture.WaveFormat.Channels;
            capture.DataAvailable += (_, args) =>
            {
                var samples = MemoryMarshal.Cast<byte, float>(args.Buffer.AsSpan(0, args.BytesRecorded));
                for (int i = 0; i + channels <= samples.Length; i += channels)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++) sum += samples[i + c];
                    _fftBuffer[_fftWritePos] = sum / channels;
                    _fftWritePos = (_fftWritePos + 1) % FftSize;
                }
                _lastAudioTick = Environment.TickCount64;
            };
            capture.StartRecording();
            _capture = capture;
        }
        catch
        {
            capture?.Dispose();
            _capture = null;
        }
    }

    private void RestartAudio()
    {
        if (_isDisposed) return;
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        Array.Clear(_fftBuffer);
        SetupAudio();
    }

    private void SetupRenderTimer()
        => _frameInterval = Stopwatch.Frequency / _settings.Fps;

    private void RenderLoop()
    {
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, TimerHighResolution, TimerAllAccess);
        if (timer == IntPtr.Zero)
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);

        long next = Stopwatch.GetTimestamp();
        while (!_isDisposed)
        {
            next += _frameInterval;
            long wait = next - Stopwatch.GetTimestamp();
            if (wait <= 0)
            {
                next = Stopwatch.GetTimestamp();
            }
            else
            {
                long due = -(wait * 10_000_000 / Stopwatch.Frequency);
                SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                WaitForSingleObject(timer, WaitInfinite);
            }

            if (!IsHandleCreated || Interlocked.Exchange(ref _framePending, 1) != 0) continue;
            try { BeginInvoke(new Action(RenderFrame)); }
            catch (InvalidOperationException) { break; }
        }

        CloseHandle(timer);
    }

    private void RenderFrame()
    {
        _framePending = 0;
        if (Environment.TickCount64 - _lastAudioTick > 200) Array.Clear(_fftBuffer);
        ProcessSpectrum(_settings);
        UpdateMarquee();
        Invalidate();
    }

    private async Task RefreshMediaAsync()
    {
        if (_isDisposed || _isRefreshingMedia || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        _isRefreshingMedia = true;
        try
        {
            _mediaManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = FindPlayingSpotifySession(_mediaManager) ?? _mediaManager.GetCurrentSession();

            if (session is null) { SetMedia(MediaSnapshot.Empty); return; }

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) { SetMedia(MediaSnapshot.Empty); return; }

            Image? albumArt = null;
            if (props.Thumbnail is not null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var reader = new DataReader(stream);
                var bytes = new byte[await reader.LoadAsync((uint)stream.Size)];
                reader.ReadBytes(bytes);
                using var temp = Image.FromStream(new MemoryStream(bytes));
                albumArt = new Bitmap(temp);
            }

            try
            {
                var      timeline    = session.GetTimelineProperties();
                TimeSpan newPosition = timeline.Position;
                TimeSpan newDuration = timeline.EndTime - timeline.StartTime;
                bool     playing     = session.GetPlaybackInfo().PlaybackStatus ==
                                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                _mediaDuration = newDuration;

                if (_anchorSetAt == DateTime.MinValue || !playing || !_isPlaying)
                {
                    _positionAnchor = newPosition;
                    _anchorSetAt    = DateTime.Now;
                }
                else
                {
                    TimeSpan estimate = _positionAnchor + (DateTime.Now - _anchorSetAt);
                    double   diffSecs = Math.Abs((newPosition - estimate).TotalSeconds);

                    if (diffSecs > SeekThresholdSeconds)
                    {
                        _positionAnchor = newPosition;
                        _anchorSetAt    = DateTime.Now;
                    }
                }

                _isPlaying = playing;
                if (playing) _lastPlayingTick = Environment.TickCount64;
            }
            catch { }

            SetMedia(new MediaSnapshot(
                string.IsNullOrWhiteSpace(props.Title)  ? null : props.Title,
                string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist,
                session.SourceAppUserModelId,
                albumArt));
        }
        catch
        {
            _mediaManager = null;
            SetMedia(MediaSnapshot.Empty);
        }
        finally { _isRefreshingMedia = false; }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static GlobalSystemMediaTransportControlsSession? FindPlayingSpotifySession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        foreach (var s in manager.GetSessions())
            if (!string.IsNullOrWhiteSpace(s.SourceAppUserModelId) &&
                s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) &&
                s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return s;
        return null;
    }

    private void SetMedia(MediaSnapshot snapshot)
    {
        _media.AlbumArt?.Dispose();
        _media = snapshot;
        if (ReferenceEquals(snapshot, MediaSnapshot.Empty)) _mediaDuration = TimeSpan.Zero;
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
    }

    private void ReloadSettings()
    {
        try
        {
            var n = new VisualizerSettings(_settingsPath);
            _settings  = n;
            _stayOnTop = n.Stayontop;

            _barBrush.Dispose();
            _barBrush = new SolidBrush(n.BarColor);

            Array.Resize(ref _smoothedLevels, n.BarCount);
            Array.Resize(ref _peakLevels,     n.BarCount);
            Array.Resize(ref _rawTargets,      n.BarCount);

            _dynamicCeiling  = 0.01f;
            _marqueeActive   = false;
            _marqueeOffset   = 0f;
            _marqueeWait     = 0f;
            _lastMarqueeText = "";

            ApplyWindowBounds();
            SetupRenderTimer();
            ApplyStayOnTop();
            Invalidate();
        }
        catch (Exception ex)
        {
            _trayIcon?.ShowBalloonTip(3000, "Audio Visualizer",
                $"Couldn't reload settings.ini:\n{ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ToggleStayOnTop()
    {
        _stayOnTop = !_stayOnTop;
        ApplyStayOnTop();
        Invalidate();
    }

    private void ApplyStayOnTop()
    {
        if (!IsHandleCreated) return;
        SetWindowPos(Handle,
            _stayOnTop ? HwndTopmost : HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void DisposeResources()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _mediaTimer.Stop();
        _mediaTimer.Dispose();
        _reloadDebounce.Dispose();
        _settingsWatcher.EnableRaisingEvents = false;
        _settingsWatcher.Dispose();
        _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceWatcher);
        _deviceEnumerator.Dispose();
        _capture?.StopRecording();
        _capture?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _barBrush.Dispose();
        _mediaFont.Dispose();

        _media.AlbumArt?.Dispose();
        _media = MediaSnapshot.Empty;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private sealed record MediaSnapshot(string? Title, string? Artist, string? SourceApp, Image? AlbumArt)
    {
        public static MediaSnapshot Empty { get; } = new(null, null, null, null);
    }

    private sealed class DefaultDeviceWatcher(Action onDefaultOutputChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) onDefaultOutputChanged();
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
