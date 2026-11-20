using IniParser;
using IniParser.Model;

namespace AudioVisualizer;

public sealed class AppSettings
{
    private readonly IniData _data;

    public bool  Stayontop { get; }
    public Color Color     { get; }

    public VisualizerSettings Visualizer { get; }

    public AppSettings(string iniPath)
    {
        if (!File.Exists(iniPath))
            throw new FileNotFoundException("Could not find settings file.", iniPath);

        IniData data = _data = new FileIniDataParser().ReadFile(iniPath);

        Stayontop = Ini.ReadBool(data, "config", "stayontop", true);

        byte r = Ini.ReadByte(data, "color", "r", 30);
        byte g = Ini.ReadByte(data, "color", "g", 144);
        byte b = Ini.ReadByte(data, "color", "b", 155);
        byte a = Ini.ReadByte(data, "color", "a", 210);
        Color = Color.FromArgb(a, r, g, b);

        Visualizer = new VisualizerSettings(data);
    }

    public bool OverlayOn(string key) => Ini.ReadBool(_data, "overlays", key, true);
}

public sealed class VisualizerSettings
{
    public int    Fps             { get; }
    public int    BarCount        { get; }
    public float  Sensitivity     { get; }
    public int    Width           { get; }
    public int    Height          { get; }
    public int    Barspacing      { get; }
    public bool   Showpeakbars    { get; }
    public bool   Showprogressbar { get; }
    public string Position        { get; }

    public bool Mediainfo      { get; }
    public bool Showtitle      { get; }
    public bool Showsourceicon { get; }

    public VisualizerSettings(IniData data)
    {
        Fps          = Math.Clamp(Ini.ReadInt  (data, "config", "fps",          90),  15, 240);
        BarCount     = Math.Clamp(Ini.ReadInt  (data, "config", "bars",         150), 1, 1000);
        Sensitivity  =            Ini.ReadFloat(data, "config", "sensitivity",  75f);
        Width        = Math.Max  (Ini.ReadInt  (data, "config", "width",        550), 100);
        Height       = Math.Max  (Ini.ReadInt  (data, "config", "height",       150), 50);
        Barspacing   = Math.Max  (Ini.ReadInt  (data, "config", "barspacing",   0),   0);
        Showpeakbars =            Ini.ReadBool (data, "config", "showpeakbars", true);

        Position = (Ini.RawValue(data, "config", "position") ?? "bottom-right").ToLowerInvariant();

        Mediainfo       = Ini.ReadBool(data, "mediainfo", "mediainfo",      true);
        Showtitle       = Ini.ReadBool(data, "mediainfo", "showtitle",      true);
        Showsourceicon  = Ini.ReadBool(data, "mediainfo", "showsourceicon", true);
        Showprogressbar = Ini.ReadBool(data, "mediainfo", "showprogressbar",
                          Ini.ReadBool(data, "config",    "showprogressbar", true));
    }
}

internal static class Ini
{
    public static string? RawValue(IniData data, string section, string key)
    {
        var sec = data[section];
        if (sec is null) return null;
        foreach (var kv in sec)
            if (string.Equals(kv.KeyName.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                return kv.Value?.Trim();
        return null;
    }

    public static int ReadInt(IniData data, string section, string key, int fallback)
        => int.TryParse(RawValue(data, section, key), out int v) ? v : fallback;

    public static float ReadFloat(IniData data, string section, string key, float fallback)
        => float.TryParse(
               RawValue(data, section, key),
               System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture,
               out float v)
           ? v : fallback;

    public static bool ReadBool(IniData data, string section, string key, bool fallback)
        => bool.TryParse(RawValue(data, section, key), out bool v) ? v : fallback;

    public static byte ReadByte(IniData data, string section, string key, byte fallback)
        => byte.TryParse(RawValue(data, section, key), out byte v) ? v : fallback;

    public static void SetValue(string path, string section, string key, string value)
    {
        var lines = File.ReadAllLines(path).ToList();
        string line = $"{key}={value}";

        int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.InsertRange(0, [$"[{section}]", line, ""]);
            File.WriteAllLines(path, lines);
            return;
        }

        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        int at = lines.FindIndex(start + 1, end - start - 1,
            l => l.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));

        if (at >= 0)
        {
            lines[at] = line;
        }
        else
        {
            int insert = end;
            while (insert > start + 1 && string.IsNullOrWhiteSpace(lines[insert - 1])) insert--;
            lines.Insert(insert, line);
        }

        File.WriteAllLines(path, lines);
    }
}
