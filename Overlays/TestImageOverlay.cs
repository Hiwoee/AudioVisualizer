namespace AudioVisualizer;

public sealed class TestImageOverlay : OverlayWindow
{
    private readonly Image? _image = LoadImage("cat.png");

    public TestImageOverlay(AppSettings settings) => ApplySettings(settings);

    public override void ApplySettings(AppSettings settings)
    {
        Place("bottom-left", _image?.Width ?? 1, _image?.Height ?? 1);
        Redraw();
    }

    protected override void Draw(Graphics g)
    {
        if (_image is not null) g.DrawImage(_image, 0, 0, Width, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }

    private static Image? LoadImage(string file)
    {
        using var stream = typeof(TestImageOverlay).Assembly.GetManifestResourceStream($"AudioVisualizer.icons.{file}");
        if (stream is null) return null;

        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}
