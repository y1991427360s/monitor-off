using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MonitorOff;

public sealed class OffRecord
{
    public string Key { get; set; } = "";      // 显示器设备路径（稳定，不随 DISPLAYn 编号变化）
    public string Name { get; set; } = "";
    public string Method { get; set; } = "";   // ddc / detach
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public sealed class Settings
{
    public bool KeepOff { get; set; } = true;
    public bool AutoWake { get; set; } = false;
    public Dictionary<string, bool> DdcCache { get; set; } = new();
    public List<OffRecord> Off { get; set; } = new();
    /// <summary>实体屏关闭方式：false=DDC 待机，true=断开输出（更彻底）</summary>
    public bool PreferDetach { get; set; } = true;
    /// <summary>正常状态（没有断开任何屏）下的布局：设备路径 → [x, y, w, h]。用于固定布局图位置和编号。</summary>
    public Dictionary<string, int[]> Home { get; set; } = new();
    /// <summary>第一次断开前的完整显示配置（PATH_INFO/MODE_INFO 原始字节，Base64），恢复时原样套用</summary>
    public string? SnapPaths { get; set; }
    public string? SnapModes { get; set; }
}

public static class Store
{
    static readonly object Gate = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorOff");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static Settings? _cache;

    public static Settings Load()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(FilePath))
                    _cache = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch { }
            _cache ??= new Settings();
            return _cache;
        }
    }

    public static void Update(Action<Settings> change)
    {
        lock (Gate)
        {
            var s = Load();
            change(s);
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }
    }

    public static List<OffRecord> OffSnapshot()
    {
        lock (Gate) return new List<OffRecord>(Load().Off);
    }
}
