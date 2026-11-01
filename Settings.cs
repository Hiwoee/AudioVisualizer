using System.Drawing;
using System.IO;
using IniParser;
using IniParser.Model;

namespace AudioVisualizer;

public class VisualizerSettings
{
    private readonly FileIniDataParser _parser = new();
    private readonly string            _iniPath;

    public int    Fps          { get; private set; }
    public int    BarCount     { get; private set; }
    public float  Sensitivity  { get; private set; }
    public int    Width        { get; private set; }
    public int    Height       { get; private set; }
    public int    Barspacing   { get; private set; }
    public bool   Showpeakbars { get; private set; }
    public bool   Showprogressbar { get; private set; }
    public bool   Stayontop    { get; private set; }

    public string Position { get; private set; } = "center";

    public bool Mediainfo { get; private set; }
    public bool Showtitle { get; private set; }

    public Color BarColor { get; private set; }

    public VisualizerSettings(string iniPath)
    {
        _iniPath = iniPath;
        if (!File.Exists(iniPath))
            throw new FileNotFoundException("Could not find settings file.", iniPath);
        Load();
    }

    public void Load()
    {
        IniData data = _parser.ReadFile(_iniPath);

        Fps          = Math.Clamp(ReadInt  (data, "config", "fps",          90),   1, 240);
        BarCount     = Math.Clamp(ReadInt  (data, "config", "bars",         150),  1, 1000);
        Sensitivity  =            ReadFloat(data, "config", "sensitivity",  75f);
        Width        = Math.Max  (ReadInt  (data, "config", "width",        550),  100);
        Height       = Math.Max  (ReadInt  (data, "config", "height",       150),  50);
        Barspacing   = Math.Max  (ReadInt  (data, "config", "barspacing",   0),    0);
        Showpeakbars =            ReadBool (data, "config", "showpeakbars", true);
        Stayontop    =            ReadBool (data, "config", "stayontop",    true);

        Position = (RawValue(data, "config", "position") ?? "bottom-right").ToLowerInvariant();

        Mediainfo = ReadBool(data, "mediainfo", "mediainfo", true);
        Showtitle = ReadBool(data, "mediainfo", "showtitle", true);
        Showprogressbar = ReadBool(data, "mediainfo", "showprogressbar",
                          ReadBool(data, "config", "showprogressbar", true));

        byte r = ReadByte(data, "color", "r", 30);
        byte g = ReadByte(data, "color", "g", 144);
        byte b = ReadByte(data, "color", "b", 155);
        byte a = ReadByte(data, "color", "a", 210);
        BarColor = Color.FromArgb(a, r, g, b);
    }

    private static string? RawValue(IniData data, string section, string key)
    {
        var sec = data[section];
        if (sec is null) return null;
        foreach (var kv in sec)
            if (string.Equals(kv.KeyName.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                return kv.Value?.Trim();
        return null;
    }

    private static int ReadInt(IniData data, string section, string key, int fallback)
        => int.TryParse(RawValue(data, section, key), out int v) ? v : fallback;

    private static float ReadFloat(IniData data, string section, string key, float fallback)
        => float.TryParse(
               RawValue(data, section, key),
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out float v)
           ? v : fallback;

    private static bool ReadBool(IniData data, string section, string key, bool fallback)
        => bool.TryParse(RawValue(data, section, key), out bool v) ? v : fallback;

    private static byte ReadByte(IniData data, string section, string key, byte fallback)
        => byte.TryParse(RawValue(data, section, key), out byte v) ? v : fallback;
}
