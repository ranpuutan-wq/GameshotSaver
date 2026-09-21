using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class AppConfig
{
    public string BaseDir { get; set; } = @"D:\Picture\AutoShot";
    public List<RouteItem> Routes { get; set; } = new();
}

public class RouteItem
{
    public string Process { get; set; } = "";   // 例: GenshinImpact
    public string Folder { get; set; } = "";   // 例: Genshin
}

public static class ConfigManager
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoShot");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig LoadOrCreate()
    {
        Directory.CreateDirectory(ConfigDir);
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                Normalize(cfg);
                return cfg;
            }
            catch { /* 壊れてたら初期化 */ }
        }
        var def = CreateDefault();
        Save(def);
        return def;
    }

    public static void Save(AppConfig cfg)
    {
        Normalize(cfg);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, json, Encoding.UTF8);
    }

    public static AppConfig CreateDefault() => new AppConfig
    {
        BaseDir = @"D:\Picture\AutoShot",
        Routes = new List<RouteItem>
        {
            new() { Process = "GenshinImpact", Folder = "Genshin" },
            new() { Process = "YuanShen",      Folder = "Genshin" },
            new() { Process = "StarRail",      Folder = "StarRail" },
            new() { Process = "msedge",        Folder = "Web" },
            new() { Process = "chrome",        Folder = "Web" },
        }
    };

    private static void Normalize(AppConfig cfg)
    {
        cfg.BaseDir = string.IsNullOrWhiteSpace(cfg.BaseDir) ? @"D:\Picture\AutoShot" : cfg.BaseDir.Trim();
        // 空行や重複を除去（Process名の大文字小文字は無視）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cfg.Routes = cfg.Routes
            .Where(r => !string.IsNullOrWhiteSpace(r.Process) && !string.IsNullOrWhiteSpace(r.Folder))
            .Where(r => seen.Add(r.Process.Trim()))
            .Select(r => new RouteItem { Process = r.Process.Trim(), Folder = r.Folder.Trim() })
            .ToList();
    }
}