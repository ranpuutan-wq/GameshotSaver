using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class AppConfig
{
    public string BaseDir { get; set; } = "";
    public List<RouteItem> Routes { get; set; } = new();

    /// <summary>
    /// 初回の保存ベースフォルダ：ユーザーのピクチャフォルダ\GameshotSaver（OneDrive等へ移動されていても追従）。
    /// ピクチャフォルダが取得できない／作成できない場合は空欄を返す（設定画面でユーザーに指定してもらう）。
    /// </summary>
    public static string CreateDefaultBaseDir()
    {
        try
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures) || !Directory.Exists(pictures)) return "";

            var dir = Path.Combine(pictures, "GameshotSaver");
            Directory.CreateDirectory(dir);
            return Directory.Exists(dir) ? dir : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>保存ベースフォルダが使える状態か（空欄・相対パス・実在しない場合は false）</summary>
    public static bool IsUsableBaseDir(string? dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                && Path.IsPathFullyQualified(dir)
                && Directory.Exists(dir);
        }
        catch
        {
            return false;
        }
    }
}

public class RouteItem
{
    public string Process { get; set; } = "";   // 例: GenshinImpact
    public string Folder { get; set; } = "";   // 例: Genshin
}

public static class ConfigManager
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameshotSaver");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig LoadOrCreate()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                Normalize(cfg);
                return cfg;
            }
            catch
            {
                // 壊れていたら初期化。ただし元のファイルは上書きせず退避しておく
                try { File.Copy(ConfigPath, ConfigPath + ".broken", overwrite: true); } catch { }
            }
        }

        var def = CreateDefault();
        try { Save(def); } catch { /* 保存できなくても既定値で起動は継続 */ }
        return def;
    }

    /// <summary>設定を保存する。失敗時は例外を投げる（呼び出し側でユーザーに通知する）</summary>
    public static void Save(AppConfig cfg)
    {
        Normalize(cfg);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, json, Encoding.UTF8);
    }

    public static AppConfig CreateDefault() => new AppConfig
    {
        BaseDir = AppConfig.CreateDefaultBaseDir(),
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
        // 空欄は空欄のまま保持する（キャプチャ時にエラー扱い）
        cfg.BaseDir = (cfg.BaseDir ?? "").Trim();
        // 空行や重複を除去（Process名の大文字小文字は無視）。JSONで null が来ても落ちないようにする
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        cfg.Routes = (cfg.Routes ?? new List<RouteItem>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Process) && !string.IsNullOrWhiteSpace(r.Folder))
            .Where(r => seen.Add(r.Process.Trim()))
            .Select(r => new RouteItem { Process = r.Process.Trim(), Folder = r.Folder.Trim() })
            .ToList();
    }
}
