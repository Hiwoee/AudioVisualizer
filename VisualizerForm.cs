//notes ble ble bla bla
//1.fix wave mode
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;
using Windows.Media.Control;

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

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndBottom  = new(1);

    private const uint SwpNoSize       = 0x0001;
    private const uint SwpNoMove       = 0x0002;
    private const uint SwpNoActivate   = 0x0010;
    private const int  WsExLayered     = 0x00080000;
    private const int  WsExToolWindow  = 0x00000080;
    private const int  WsExTransparent = 0x00000020;
    private const uint LwaColorKey     = 0x00000001;

    private const int FftSize = 4096;

    private readonly float[]   _fftBuffer  = new float[FftSize];
    private readonly Complex[] _fftComplex = new Complex[FftSize];
    private readonly float[]   _magnitudes = new float[FftSize / 2];
    private          int       _fftWritePos;

    private float[] _smoothedLevels;
    private float[] _peakLevels;
    private float[] _rawTargets;

    private float _vuSmoothed;
    private float _dynamicCeiling = 0.01f;

    private static readonly float[] VuVariations = BuildVuVariations(1000);

    private static float[] BuildVuVariations(int count)
    {
        var rng = new Random(1234);
        var v   = new float[count];
        for (int i = 0; i < count; i++)
            v[i] = 0.92f + (float)rng.NextDouble() * 0.16f;
        return v;
    }

    private string _activeMode;

    private static readonly (string Name, Color Value)[] ColorPresets =
    {
        ("Purple", Color.FromArgb(210, 138,  43, 226)),
        ("Orange", Color.FromArgb(210, 220,  80,  30)),
        ("Green",  Color.FromArgb(210,  30, 180,  80)),
        ("Pink",   Color.FromArgb(210, 200,  40,  80)),
        ("Blue",   Color.FromArgb(210,  50, 120, 220)),
        ("White",  Color.FromArgb(180, 210, 210, 210)),
    };
    private int _presetIndex;

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
    private readonly object _settingsLock = new();
    private readonly object _mediaLock    = new();

    private VisualizerSettings _settings;
    private bool               _stayOnTop;
    private SolidBrush         _barBrush;

    private WasapiLoopbackCapture? _capture;

    private NotifyIcon?                         _trayIcon;
    private ToolStripMenuItem?                  _modeMenuItem;
    private ToolStripMenuItem?                  _presetMenuItem;
    private readonly FileSystemWatcher          _settingsWatcher;
    private System.Windows.Forms.Timer?         _renderTimer;
    private readonly System.Windows.Forms.Timer _mediaTimer;

    private readonly Font  _mediaFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Brush _peakBrush = Brushes.White;
    private readonly Brush _textBrush = Brushes.White;

    private MediaSnapshot _media = MediaSnapshot.Empty;
    private bool          _isReloadPending;
    private bool          _isDisposed;

    public VisualizerForm(VisualizerSettings settings, string settingsPath)
    {
        _settings     = settings;
        _settingsPath = settingsPath;
        _stayOnTop    = _settings.Stayontop;
        _barBrush     = new SolidBrush(_settings.BarColor);
        _activeMode   = _settings.Mode;

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

        SetupAudio();
        SetupRenderTimer();
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

        VisualizerSettings settings;
        lock (_settingsLock) { settings = _settings; }

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

        DrawMediaInfo(g, settings);
        DrawProgressBar(g, settings);
    }

    private void ProcessAudio()
    {
        VisualizerSettings settings;
        string mode;
        lock (_settingsLock) { settings = _settings; mode = _activeMode; }

        if (mode == "wave")
            ProcessVU(settings);
        else
            ProcessSpectrum(settings);
    }

    private static float HzToMel(float hz) => 2595f * MathF.Log10(1f + hz / 700f);
    private static float MelToHz(float mel) => 700f * (MathF.Pow(10f, mel / 2595f) - 1f);

    private void ProcessVU(VisualizerSettings settings)
    {
        float peak  = 0f;
        float sumSq = 0f;

        for (int i = 0; i < FftSize; i++)
        {
            float s = _fftBuffer[i];
            sumSq += s * s;
            peak   = Math.Max(peak, Math.Abs(s));
        }

        float rms      = MathF.Sqrt(sumSq / FftSize);
        float combined = rms * 0.5f + peak * 0.5f;

        float db    = combined > 0f ? 20f * MathF.Log10(combined) : -120f;
        float level = Math.Clamp((db + 60f) / 60f, 0f, 1f);

        _vuSmoothed = _vuSmoothed < level
            ? Lerp(_vuSmoothed, level, 0.9f)
            : Lerp(_vuSmoothed, level, 0.08f);

        _dynamicCeiling = _vuSmoothed > _dynamicCeiling
            ? Lerp(_dynamicCeiling, _vuSmoothed, 0.25f)
            : Lerp(_dynamicCeiling, _vuSmoothed, 0.01f);
        _dynamicCeiling = Math.Max(_dynamicCeiling, 0.01f);

        float target = Math.Clamp(
            _vuSmoothed / _dynamicCeiling * (settings.Sensitivity / 100f),
            0f, 1f);

        int barCount = Math.Max(1, settings.BarCount);

        for (int i = 0; i < barCount; i++)
        {
            float variation   = i < VuVariations.Length ? VuVariations[i] : 1f;
            float finalTarget = target * variation;

            _smoothedLevels[i] = _smoothedLevels[i] < finalTarget
                ? Lerp(_smoothedLevels[i], finalTarget, 0.95f)
                : Lerp(_smoothedLevels[i], finalTarget, 0.12f);

            float barHeight = _smoothedLevels[i] * Height;

            if (barHeight > _peakLevels[i])
                _peakLevels[i] = barHeight;
            else
                _peakLevels[i] *= 0.94f;
        }
    }

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
            final *= 0.97f + 0.03f * MathF.Sin(i * 0.2f + _vuSmoothed * 5f);

            _rawTargets[i] = final;
        }

        ApplyGaussianSmooth(barCount, radius: 1, sigma: 0.8f);
        ApplyDynamicScale(settings, barCount);
    }

    private void RunFFT()
    {
        for (int i = 0; i < FftSize; i++)
        {
            double hann = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            _fftComplex[i].X = _fftBuffer[(_fftWritePos + i) % FftSize] * (float)hann;
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

        MediaSnapshot media;
        lock (_mediaLock) { media = _media.CloneShallow(); }
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

        TimeSpan anchor;
        TimeSpan duration;
        DateTime anchorTime;
        lock (_mediaLock)
        {
            anchor     = _positionAnchor;
            duration   = _mediaDuration;
            anchorTime = _anchorSetAt;
        }

        if (duration.TotalSeconds <= 0 || anchorTime == DateTime.MinValue) return;

        TimeSpan current  = anchor + (DateTime.Now - anchorTime);
        double   fraction = Math.Clamp(current.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);

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

        _modeMenuItem = new ToolStripMenuItem(ModeMenuLabel())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        };
        _modeMenuItem.Click += (_, _) => ToggleMode();
        menu.Items.Add(_modeMenuItem);

        _presetMenuItem = new ToolStripMenuItem(PresetMenuLabel());
        _presetMenuItem.Click += (_, _) => CycleColorPreset();
        menu.Items.Add(_presetMenuItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reload settings",    null, (_, _) => ReloadSettings());
        menu.Items.Add("Toggle stay on top", null, (_, _) => ToggleStayOnTop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ToggleStayOnTop();
    }

    private void ToggleMode()
    {
        lock (_settingsLock)
            _activeMode = _activeMode == "wave" ? "spectrum" : "wave";

        _dynamicCeiling = 0.01f;
        _vuSmoothed     = 0f;

        if (_modeMenuItem is not null)
            _modeMenuItem.Text = ModeMenuLabel();

        Invalidate();
    }

    private string ModeMenuLabel()
    {
        string current = _activeMode == "wave" ? "Wave (VU)" : "Spectrum";
        string next    = _activeMode == "wave" ? "Spectrum"  : "Wave (VU)";
        return $"Current Mode: {current}";
    }

    private void CycleColorPreset()
    {
        _presetIndex = (_presetIndex + 1) % (ColorPresets.Length + 1);

        Color newColor = _presetIndex == 0
            ? _settings.BarColor
            : ColorPresets[_presetIndex - 1].Value;

        _barBrush.Dispose();
        _barBrush = new SolidBrush(newColor);

        if (_presetMenuItem is not null)
            _presetMenuItem.Text = PresetMenuLabel();

        Invalidate();
    }

    private string PresetMenuLabel()
    {
        string name = _presetIndex == 0
            ? "Default (INI)"
            : ColorPresets[_presetIndex - 1].Name;
        return $"Colour: {name} preset";
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
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += (_, args) =>
        {
            int floatCount = args.BytesRecorded / sizeof(float);
            for (int i = 0; i < floatCount; i += 2)
            {
                float left  = BitConverter.ToSingle(args.Buffer, i * 4);
                float right = BitConverter.ToSingle(args.Buffer, (i + 1) * 4);
                _fftBuffer[_fftWritePos % FftSize] = (left + right) * 0.5f;
                _fftWritePos++;
            }
        };
        _capture.RecordingStopped += (_, _) => { };
        _capture.StartRecording();
    }

    private void SetupRenderTimer()
    {
        _renderTimer?.Stop();
        _renderTimer?.Dispose();

        int intervalMs = Math.Max(1, (int)Math.Round(1000d / Math.Clamp(_settings.Fps, 15, 240)));
        _renderTimer   = new System.Windows.Forms.Timer { Interval = intervalMs };
        _renderTimer.Tick += (_, _) =>
        {
            ProcessAudio();
            UpdateMarquee();
            Invalidate();
        };
        _renderTimer.Start();
    }

    private async Task RefreshMediaAsync()
    {
        if (_isDisposed) return;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = FindSpotifySession(manager) ?? manager.GetCurrentSession();

            if (session is null) { SetMedia(MediaSnapshot.Empty); return; }

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) { SetMedia(MediaSnapshot.Empty); return; }

            Image? albumArt = null;
            if (props.Thumbnail is not null)
            {
                using var stream        = await props.Thumbnail.OpenReadAsync();
                using var contentStream = stream.AsStreamForRead();
                using var temp          = Image.FromStream(contentStream);
                albumArt = new Bitmap(temp);
            }

            try
            {
                var      timeline    = session.GetTimelineProperties();
                TimeSpan newPosition = timeline.Position;
                TimeSpan newDuration = timeline.EndTime - timeline.StartTime;

                lock (_mediaLock)
                {
                    _mediaDuration = newDuration;

                    if (_anchorSetAt == DateTime.MinValue)
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
                }
            }
            catch { }

            SetMedia(new MediaSnapshot(
                string.IsNullOrWhiteSpace(props.Title)  ? null : props.Title,
                string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist,
                session.SourceAppUserModelId,
                albumArt));
        }
        catch { SetMedia(MediaSnapshot.Empty); }
    }

    private static GlobalSystemMediaTransportControlsSession? FindSpotifySession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        foreach (var s in manager.GetSessions())
            if (!string.IsNullOrWhiteSpace(s.SourceAppUserModelId) &&
                s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private void SetMedia(MediaSnapshot snapshot)
    {
        lock (_mediaLock) { _media.AlbumArt?.Dispose(); _media = snapshot; }
    }

    private void OnSettingsFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isReloadPending || _isDisposed) return;
        _isReloadPending = true;
        BeginInvoke(new Action(() =>
        {
            try     { ReloadSettings(); }
            finally { _isReloadPending = false; }
        }));
    }

    private void ReloadSettings()
    {
        try
        {
            var n = new VisualizerSettings(_settingsPath);
            lock (_settingsLock)
            {
                _settings   = n;
                _stayOnTop  = n.Stayontop;
                _activeMode = n.Mode;

                if (_presetIndex == 0)
                {
                    _barBrush.Dispose();
                    _barBrush = new SolidBrush(n.BarColor);
                }

                Array.Resize(ref _smoothedLevels, n.BarCount);
                Array.Resize(ref _peakLevels,     n.BarCount);
                Array.Resize(ref _rawTargets,      n.BarCount);
            }

            _dynamicCeiling  = 0.01f;
            _vuSmoothed      = 0f;
            _marqueeActive   = false;
            _marqueeOffset   = 0f;
            _marqueeWait     = 0f;
            _lastMarqueeText = "";

            if (_modeMenuItem is not null)
                _modeMenuItem.Text = ModeMenuLabel();

            ApplyWindowBounds();
            SetupRenderTimer();
            ApplyStayOnTop();
            Invalidate();
        }
        catch { }
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
        _renderTimer?.Stop();
        _renderTimer?.Dispose();
        _settingsWatcher.EnableRaisingEvents = false;
        _settingsWatcher.Dispose();
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

        lock (_mediaLock)
        {
            _media.AlbumArt?.Dispose();
            _media = MediaSnapshot.Empty;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private sealed record MediaSnapshot(string? Title, string? Artist, string? SourceApp, Image? AlbumArt)
    {
        public static MediaSnapshot Empty { get; } = new(null, null, null, null);
        public MediaSnapshot CloneShallow() => new(Title, Artist, SourceApp, AlbumArt);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
        EnsureDefaultSettingsFile(settingsPath);

        VisualizerSettings settings;
        try
        {
            settings = new VisualizerSettings(settingsPath);
        }
        catch (FormatException ex)
        {
            MessageBox.Show($"Settings format error:\n{ex.Message}",
                "Audio Visualizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load settings:\n{ex.Message}",
                "Audio Visualizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new VisualizerForm(settings, settingsPath));
    }

    private static void EnsureDefaultSettingsFile(string path)
    {
        if (File.Exists(path)) return;
        File.WriteAllLines(path, new[]
        {
            "; ╔══════════════════════════════════════════════════════╗",
            "; ║           Audio Visualizer - settings.ini            ║",
            "; ║  Changes apply live — no restart needed.             ║",
            "; ╚══════════════════════════════════════════════════════╝",
            "",
            "[config]",
            "",
            "; How tall bars reach (percentage, 75 = aim for ~75% height at peak)",
            "sensitivity=75",
            "",
            "; Render frame rate (15–240). Higher = smoother but more CPU.",
            "fps=90",
            "",
            "; Number of bars drawn across the window.",
            "bars=250",
            "",
            "; Window size in pixels.",
            "width=650",
            "height=150",
            "",
            "; Pixel gap between bars (0 = bars touch).",
            "barspacing=0",
            "",
            "; Show a peak dot above each bar (true/false).",
            "showpeakbars=true",
            "",
            "; Keep window above all other windows (true/false).",
            "; Can also be toggled by double-clicking the tray icon.",
            "stayontop=true",
            "",
            "; Visualizer mode:",
            ";   spectrum  —  frequency bars, bass left / treble right",
            ";   wave      —  VU meter, all bars move together with the beat",
            "; Can also be toggled live from the tray icon menu.",
            "mode=spectrum",
            "",
            "; Window corner position: bottom-right | bottom-left",
            "position=bottom-right",
            "",
            "[mediainfo]",
            "",
            "; Show the currently playing track overlay (true/false).",
            "mediainfo=true",
            "",
            "; Show song title and artist (true/false).",
            "; Long titles scroll automatically as a marquee.",
            "showtitle=true",
            "",
            "; Show a progress bar at the bottom of the window (true/false).",
            "showprogressbar=true",
            "",
            "[color]",
            "",
            "; Bar colour as RGBA (0–255 each). a = opacity (255 = fully opaque).",
            "; Colour presets can also be cycled live from the tray icon menu.",
            "r=30",
            "g=144",
            "b=155",
            "a=210"
        });
    }
}