using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AudioVisualizer;

public abstract class OverlayWindow : Form
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int    Size;
        public int    Width;
        public int    Height;
        public short  Planes;
        public short  BitCount;
        public uint   Compression;
        public uint   SizeImage;
        public int    XPelsPerMeter;
        public int    YPelsPerMeter;
        public uint   ClrUsed;
        public uint   ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfoHeader bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndBottom  = new(1);

    private const uint SwpNoSize       = 0x0001;
    private const uint SwpNoMove       = 0x0002;
    private const uint SwpNoActivate   = 0x0010;
    private const int  WsExLayered     = 0x00080000;
    private const int  WsExToolWindow  = 0x00000080;
    private const int  WsExTransparent = 0x00000020;
    private const uint UlwAlpha        = 0x00000002;
    private const byte AcSrcAlpha      = 0x01;
    private const int  GwlpHwndParent  = -8;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    // for now till i make it so you can drag them where you want (still not to make stuff like audio visualizer actually flip and such)
    private static readonly string[] Positions =
    [
        "top-left",    "top",    "top-right",
        "left",        "center", "right",
        "bottom-left", "bottom", "bottom-right",
    ];

    private bool    _stayOnTop;
    private string  _position = "";
    private int     _monitor;
    private IntPtr  _surfaceDc;
    private IntPtr  _surfaceDib;
    private IntPtr  _surfaceOld;
    private Bitmap? _surface;

    protected OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;

        HandleCreated += (_, _) =>
        {
            ApplyStayOnTop();
            Redraw();
        };
    }

    public abstract void ApplySettings(AppSettings settings);

    protected abstract void Draw(Graphics g);

    public void SetStayOnTop(bool stayOnTop)
    {
        _stayOnTop = stayOnTop;
        ApplyStayOnTop();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExToolWindow | WsExTransparent;
            return cp;
        }
    }

    public void Reposition()
    {
        Place(_position, Width, Height, _monitor);
        Redraw();
    }

    protected void Place(string position, int width, int height, int monitor = 0)
    {
        _position = position;
        _monitor  = monitor;

        if (!Positions.Contains(position)) position = "bottom-right";

        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        var screen  = monitor >= 1 && monitor <= screens.Length ? screens[monitor - 1] : Screen.PrimaryScreen ?? screens[0];
        var area    = screen.WorkingArea;
        Size = new Size(width, height);

        int x = position.EndsWith("left")    ? area.Left
              : position.EndsWith("right")   ? area.Right - width
              :                                area.Left + (area.Width - width) / 2;
        int y = position.StartsWith("top")    ? area.Top
              : position.StartsWith("bottom") ? area.Bottom - height
              :                                 area.Top + (area.Height - height) / 2;
        Location = new Point(x, y);
    }

    protected void Redraw()
    {
        if (IsDisposed || Disposing || !IsHandleCreated || Width <= 0 || Height <= 0) return;

        if (_surface is null || _surface.Width != Width || _surface.Height != Height)
            CreateSurface(Width, Height);

        using (var g = Graphics.FromImage(_surface!))
        {
            g.Clear(Color.Transparent);
            Draw(g);
        }

        var location = Location;
        var size     = Size;
        var source   = Point.Empty;
        var blend    = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref location, ref size, _surfaceDc, ref source, 0, ref blend, UlwAlpha);
    }

    protected override void Dispose(bool disposing)
    {
        FreeSurface();
        base.Dispose(disposing);
    }

    private void CreateSurface(int width, int height)
    {
        FreeSurface();

        var header = new BitmapInfoHeader
        {
            Size     = Marshal.SizeOf<BitmapInfoHeader>(),
            Width    = width,
            Height   = -height,
            Planes   = 1,
            BitCount = 32
        };

        _surfaceDc  = CreateCompatibleDC(IntPtr.Zero);
        _surfaceDib = CreateDIBSection(_surfaceDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        _surfaceOld = SelectObject(_surfaceDc, _surfaceDib);
        _surface    = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    private void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;

        if (_surfaceDc != IntPtr.Zero)
        {
            SelectObject(_surfaceDc, _surfaceOld);
            DeleteDC(_surfaceDc);
            _surfaceDc = IntPtr.Zero;
        }

        if (_surfaceDib != IntPtr.Zero)
        {
            DeleteObject(_surfaceDib);
            _surfaceDib = IntPtr.Zero;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TaskbarCreated) ApplyStayOnTop();
        base.WndProc(ref m);
    }

    private void ApplyStayOnTop()
    {
        if (!IsHandleCreated) return;
        SetWindowLongPtr(Handle, GwlpHwndParent, _stayOnTop ? IntPtr.Zero : FindWindow("Progman", null));
        SetWindowPos(Handle,
            _stayOnTop ? HwndTopmost : HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }
}
