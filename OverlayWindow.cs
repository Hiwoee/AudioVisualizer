using System.Runtime.InteropServices;

namespace AudioVisualizer;

public abstract class OverlayWindow : Form
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

    private bool _stayOnTop;

    protected OverlayWindow()
    {
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

        HandleCreated += (_, _) =>
        {
            SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(BackColor), 0, LwaColorKey);
            ApplyStayOnTop();
        };
    }

    public abstract void ApplySettings(AppSettings settings);

    public void SetStayOnTop(bool stayOnTop)
    {
        _stayOnTop = stayOnTop;
        ApplyStayOnTop();
        Invalidate();
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

    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.Clear(BackColor);

    protected void Place(string position, int width, int height)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        Size = new Size(width, height);

        Location = position == "bottom-left"
            ? new Point(area.Left,          area.Bottom - height)
            : new Point(area.Right - width, area.Bottom - height);
    }

    private void ApplyStayOnTop()
    {
        if (!IsHandleCreated) return;
        SetWindowPos(Handle,
            _stayOnTop ? HwndTopmost : HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }
}
