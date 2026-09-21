using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;        // ← 追加（SystemIcons用）
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Media; // ← 追加


public class SaverApp : Form
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004;
    const int HOTKEY_ID = 1;

    const int WM_CLIPBOARDUPDATE = 0x031D;

    // 追加：効果音プレイヤー
    private readonly SoundPlayer? _shutter;

    private uint _lastClipboardSeq = 0;

    // フィールド追加
    private AppConfig _cfg = new AppConfig();
    private string baseDir = @"D:\Picture\AutoShot";
    private Dictionary<string, string> routes = new(StringComparer.OrdinalIgnoreCase);


    // ▼ 追加：トレイ関連
    private NotifyIcon _tray;
    private ContextMenuStrip _menu;
    private ToolStripMenuItem _miToggle;
    private ToolStripMenuItem _miExit;
    readonly Icon _iconOn = GameshotSaver.Properties.Resources.Icon_ON;
    readonly Icon _iconOff = GameshotSaver.Properties.Resources.ICON_OFF;

    // 監視状態フラグ（ホットキー登録＆クリップボード監視のON/OFF）
    private bool _monitoringActive = false;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    //readonly string baseDir = @"D:\Picture\★ゲーム"; // ←好きな場所に変更
    //readonly Dictionary<string, string> routes = new(StringComparer.OrdinalIgnoreCase)
    //{
    //    { "GenshinImpact", "原神" }, // 原神
    //    { "StarRail",     "スターレイル" }, // スターレイル
    //    { "ZenlessZoneZero",  "ゼンゼロ" }, // 全レスジーンゼロ
    //    { "BH3",  "崩壊3rd" }, //崩壊3rd
    //};
    
    public SaverApp()
    {

        ShowInTaskbar = false; 
        WindowState = FormWindowState.Minimized; 
        Opacity = 0; // こっそり常駐

        // ▼ トレイアイコンとメニュー初期化
        _menu = new ContextMenuStrip();
        _miToggle = new ToolStripMenuItem("監視を開始");
        _miExit = new ToolStripMenuItem("終了");

        _miToggle.Click += (s, e) => UpdateMonitoring(!_monitoringActive);
        _miExit.Click += (s, e) => { Close(); };

        _menu.Items.AddRange(new ToolStripItem[] { _miToggle, new ToolStripSeparator(), _miExit });

        _tray = new NotifyIcon
        {
            Icon = GameshotSaver.Properties.Resources.Icon_ON, // Icon型リソース
            Visible = true,
            Text = "AutoShot: 起動中",
            ContextMenuStrip = _menu
        };
        this.Icon = _tray.Icon; // （任意）フォームのアイコンも同じに


        // 左クリックで監視ON/OFF
        _tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                UpdateMonitoring(!_monitoringActive);
        };


        // 追加：exeと同じフォルダの shutter.wav を使う
        //var wav = Path.Combine(AppContext.BaseDirectory, "shutter.wav");
        //if (File.Exists(wav))
        //{
        //_shutter = new SoundPlayer(wav);
        _shutter = new SoundPlayer(GameshotSaver.Properties.Resources.shutter);
        try { _shutter.LoadAsync(); } catch { /* 無視 */ }

        //    try { _shutter.LoadAsync(); } catch { /* 無視 */ }
        //}

        // ...既存初期化...
        LoadConfigAndApply();

        // ▼ トレイメニューに「設定…」を追加
        var miSettings = new ToolStripMenuItem("設定…");
        miSettings.Click += (s, e) =>
        {
            using var dlg = new SettingsForm(_cfg);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // 保存して即反映
                ConfigManager.Save(dlg.Result);
                _cfg = dlg.Result;
                baseDir = _cfg.BaseDir;
                routes = _cfg.Routes.ToDictionary(r => r.Process, r => r.Folder, StringComparer.OrdinalIgnoreCase);
                // 必要なら baseDir が変わった場合にフォルダ作成
                try { Directory.CreateDirectory(baseDir); } catch { }
            }
        };

        // 既存 _menu に挿入（先頭付近が見つけやすい）
        _menu.Items.Insert(0, miSettings);
        _menu.Items.Insert(1, new ToolStripSeparator());


    }

    // 起動時の設定ロード（コンストラクタ or OnHandleCreated の前後どちらでもOK）
    private void LoadConfigAndApply()
    {
        _cfg = ConfigManager.LoadOrCreate();
        baseDir = _cfg.BaseDir;
        routes = _cfg.Routes.ToDictionary(r => r.Process, r => r.Folder, StringComparer.OrdinalIgnoreCase);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // RegisterHotKey / AddClipboardFormatListener は呼ばない！
        //RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, (uint)Keys.V); // Ctrl+Shift+V
        //AddClipboardFormatListener(this.Handle); // ←追加：クリップボード監視を開始

        // アプリ起動時は監視を開始（好みによりfalseで始めてもOK）
        UpdateMonitoring(true);

    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { UpdateMonitoring(false); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }

        // RegisterHotKey / AddClipboardFormatListener は呼ばない！
        //RemoveClipboardFormatListener(this.Handle); // ←追加：監視解除
        //UnregisterHotKey(this.Handle, HOTKEY_ID);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        // ホットキーとクリップボード更新のメッセージを処理
        if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_ID)
        {
            TrySaveClipboardImage(onlyWhenProcIsMapped: false, dedupeBySeq: false); // ← 変更
        }
        else if (m.Msg == WM_CLIPBOARDUPDATE)
        // クリップボードが更新されたとき
        {
            // ←追加：クリップボード更新時、自動保存（routes一致時のみ）
            TrySaveClipboardImage(onlyWhenProcIsMapped: true, dedupeBySeq: true);  // ← 変更
        }
        base.WndProc(ref m);
    }

    // ▼ 監視状態の更新：ホットキー登録＆クリップボード監視をまとめてON/OFF
    private void UpdateMonitoring(bool enable)
    {
        if (enable == _monitoringActive) return;

        if (enable)
        {
            // 既存のホットキー登録（Ctrl+Shift+V 等）
            bool okHotkey = RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, (uint)Keys.V);
            AddClipboardFormatListener(this.Handle);

            _monitoringActive = true;
            if (!okHotkey)
            {
                // ここはバルーン無効にしてるので、ToolTipテキストで示すのがスマート
                _tray.Text = "AutoShot: 監視中（ホットキー未登録）";
                // あるいはメニュー項目名を変更:
                _miToggle.Text = "監視を停止（HK未登録）";
            }
            else
            {
                _tray.Text = "AutoShot: 監視中";
            }
            UpdateTrayUi("監視を開始しました", isOn: true);
        }
        else
        {
            // 監視停止：解除を忘れない
            try { RemoveClipboardFormatListener(this.Handle); } catch { }
            try { UnregisterHotKey(this.Handle, HOTKEY_ID); } catch { }

            _monitoringActive = false;
            UpdateTrayUi("監視を停止しました", isOn: false);
        }
    }



    /// <summary>
    /// クリップボードから画像を取り出して PNG で保存する。
    /// - onlyWhenProcIsMapped = true  のとき：前面プロセスが routes に載っている場合のみ保存（自動保存用）
    /// - dedupeBySeq          = true  のとき：クリップボードのシーケンス番号で二重処理を防止（自動保存用）
    ///
    /// 例）自動保存（WM_CLIPBOARDUPDATE）：TrySaveClipboardImage(true,  true)
    ///     手動保存（ホットキー）        ：TrySaveClipboardImage(false, false)
    /// </summary>
    void TrySaveClipboardImage(bool onlyWhenProcIsMapped, bool dedupeBySeq)
    {
        // ▼ 1) （自動保存時のみ）クリップボードの「更新シーケンス番号」で二重処理を防ぐ
        //    クリップボードに何かが Set されるたびに OS が番号を+1する。
        //    WM_CLIPBOARDUPDATE が短時間で複数飛んでも、同じ番号なら同一更新とみなしてスキップ。
        if (dedupeBySeq)
        {
            uint seq = GetClipboardSequenceNumber();
            if (seq == _lastClipboardSeq) return; // 直前と同じ更新 → 何もしない
            _lastClipboardSeq = seq;
        }

        // ▼ 2) 画像以外の更新（文字列やファイルなど）のときは即終了
        if (!Clipboard.ContainsImage()) return;

        // ▼ 3) クリップボードは他プロセスが一時的にロックしていることがあるので、短いリトライを入れる
        //    120ms * 最大6回 ≒ 約0.7秒の猶予。体感を損なわず成功率だけ上げる。
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                // ▼ 4) 画像を安全に取り出す
                //    Clipboard.GetImage() は Image（参照元がクリップボード）なので、即 Bitmap にコピーして
                //    using で閉じられる自己完結オブジェクトにしておく（ロック/リークを避ける）。
                var src = Clipboard.GetImage();
                if (src is null) return;

                using (var bmp = new Bitmap(src))
                {
                    // ▼ 5) 前面ウィンドウのプロセス名を取得して、保存先フォルダを振り分ける
                    //    - routes に載っていれば、そのマッピング名で保存
                    //    - 載っていなければ：
                    //        * onlyWhenProcIsMapped=true（自動保存） → 何もせず終了（対象外）
                    //        * false（ホットキー）                   → proc名 or "Unknown" で保存
                    string proc = GetActiveProcessName();

                    string sub;
                    if (routes.TryGetValue(proc, out var mapped))
                    {
                        sub = mapped; // 例: "GenshinImpact" → "Genshin"
                    }
                    else
                    {
                        if (onlyWhenProcIsMapped) return; // 対象外アプリ → 自動保存ではスキップ
                        sub = string.IsNullOrEmpty(proc) ? "Unknown" : proc;
                    }

                    // ▼ 6) 保存先ディレクトリを用意（なければ作成）
                    string dir = Path.Combine(baseDir, sub);
                    Directory.CreateDirectory(dir);

                    // ▼ 7) ファイル名はミリ秒まで入れて衝突回避（yyyyMMdd_HHmmssfff.png）
                    string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmssfff}.png");

                    // ▼ 8) PNG で保存（★ここで bmp は our-own オブジェクトなので安全に保存できる）
                    bmp.Save(path, ImageFormat.Png);
                }

                // ▼ 9) 成功したら効果音（例外が出てもアプリは落とさない実装にしてある）
                PlayShutter();
                return; // 1回成功したら抜ける
            }
            catch
            {
                // ▼ 10) 一時的なロック/競合の可能性 → 少し待ってリトライ
                Thread.Sleep(120);
            }
        }

        // ※ ここに到達＝全リトライ失敗。必要ならログや失敗通知を出す。
    }

    static string GetActiveProcessName()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "";
        GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    private void PlayShutter()
    {
        try
        {
            if (_shutter != null) _shutter.Play();   // 非同期で鳴る
            else SystemSounds.Asterisk.Play();        // フォールバック
        }
        catch { /* 音で失敗してもアプリは落とさない */ }
    }

    private void UpdateTrayUi(string msg, bool isOn)
    {
        _tray.Icon = isOn ? _iconOn : _iconOff;
        _tray.Text = isOn ? "AutoShot: 監視中" : "AutoShot: 停止中";
        _miToggle.Text = isOn ? "監視を停止" : "監視を開始";
        //_tray.BalloonTipTitle = "AutoShot";
        //_tray.BalloonTipText = msg;
        //_tray.ShowBalloonTip(1200);
    }


    // class の外（名前空間直下でもOK）
    static class SingleInstance
    {
        private static System.Threading.Mutex? _mutex;
        public static bool TryLock()
        {
            bool created;
            _mutex = new System.Threading.Mutex(true, @"Global\AutoShot_Mutex", out created);
            return created;
        }
    }

    [STAThread]
    public static void Main()
    {
        if (!SingleInstance.TryLock()) return;  // すでに起動中なら即終了
        
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SaverApp());
    }
}

