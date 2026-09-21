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
using GameshotSaver;


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
    private string baseDir = "";
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
    private bool _hotkeyRegistered = false;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

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
            Text = $"{AppInfo.DisplayName}: 起動中",
            ContextMenuStrip = _menu
        };
        this.Icon = _tray.Icon; // （任意）フォームのアイコンも同じに


        // 左クリックで監視ON/OFF
        _tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                UpdateMonitoring(!_monitoringActive);
        };


        // シャッター音はリソースのWAVを使う
        try
        {
            _shutter = new SoundPlayer(GameshotSaver.Properties.Resources.shutter);
            _shutter.LoadAsync();
        }
        catch { _shutter = null; /* 読めなければシステム音にフォールバック */ }

        LoadConfigAndApply();

        // ▼ トレイメニューに「設定…」を追加
        var miSettings = new ToolStripMenuItem("設定…");
        miSettings.Click += (s, e) =>
        {
            using var dlg = new SettingsForm(_cfg);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // 保存に失敗したら反映せず、ユーザーに知らせる
            try
            {
                ConfigManager.Save(dlg.Result);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"設定を保存できませんでした。\n{ex.Message}", AppInfo.DisplayName,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ApplyConfig(dlg.Result);
            // baseDir が変わった場合にフォルダ作成（空欄・作成失敗はキャプチャ時にエラー音で通知）
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                try { Directory.CreateDirectory(baseDir); } catch { }
            }
        };

        // 既存 _menu に挿入（先頭付近が見つけやすい）
        _menu.Items.Insert(0, miSettings);
        _menu.Items.Insert(1, new ToolStripSeparator());


    }

    // 起動時の設定ロード
    private void LoadConfigAndApply()
    {
        ApplyConfig(ConfigManager.LoadOrCreate());
    }

    private void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;
        baseDir = cfg.BaseDir ?? "";
        // 重複キーがあっても落ちないよう、後勝ちで詰める
        routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Routes)
            routes[r.Process] = r.Folder;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // アプリ起動時は監視を開始（好みによりfalseで始めてもOK）
        UpdateMonitoring(true);

    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { UpdateMonitoring(false); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _shutter?.Dispose(); } catch { }

        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        // ホットキーとクリップボード更新のメッセージを処理
        if (m.Msg == WM_HOTKEY && m.WParam == (IntPtr)HOTKEY_ID)
        {
            TrySaveClipboardImage(onlyWhenProcIsMapped: false, dedupeBySeq: false);
        }
        else if (m.Msg == WM_CLIPBOARDUPDATE)
        // クリップボードが更新されたとき
        {
            // クリップボード更新時、自動保存（routes一致時のみ）
            TrySaveClipboardImage(onlyWhenProcIsMapped: true, dedupeBySeq: true);
        }
        base.WndProc(ref m);
    }

    // ▼ 監視状態の更新：ホットキー登録＆クリップボード監視をまとめてON/OFF
    private void UpdateMonitoring(bool enable)
    {
        if (enable == _monitoringActive) return;

        if (enable)
        {
            // ホットキー（Ctrl+Shift+V）は他アプリと競合すると登録に失敗する
            _hotkeyRegistered = RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, (uint)Keys.V);
            AddClipboardFormatListener(this.Handle);

            _monitoringActive = true;
        }
        else
        {
            // 監視停止：解除を忘れない
            try { RemoveClipboardFormatListener(this.Handle); } catch { }
            try { UnregisterHotKey(this.Handle, HOTKEY_ID); } catch { }

            _hotkeyRegistered = false;
            _monitoringActive = false;
        }
        UpdateTrayUi();
    }



    /// <summary>
    /// クリップボードから画像を取り出して PNG で保存する。
    /// - onlyWhenProcIsMapped = true  のとき：前面プロセスが routes に載っている場合のみ保存（自動保存用）
    /// - dedupeBySeq          = true  のとき：クリップボードのシーケンス番号で二重処理を防止（自動保存用）
    /// - 保存ベースフォルダが空欄／実在しない場合、または保存に失敗した場合はエラー音を鳴らして中止する
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
        //    クリップボードが他プロセスにロックされていると例外になるため、その場合も対象外として終了
        try
        {
            if (!Clipboard.ContainsImage()) return;
        }
        catch { return; }

        // ▼ 3) 前面ウィンドウのプロセス名を取得して、保存先フォルダを振り分ける
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

        // ▼ 4) 保存対象と決まった時点で、保存ベースフォルダを検証
        //    空欄・相対パス・実在しない（ドライブ未接続など）場合はエラー音を鳴らして中止
        if (!AppConfig.IsUsableBaseDir(baseDir))
        {
            PlayError();
            return;
        }

        // ▼ 5) クリップボードは他プロセスが一時的にロックしていることがあるので、短いリトライを入れる
        //    120ms * 最大6回 ≒ 約0.7秒の猶予。体感を損なわず成功率だけ上げる。
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                // ▼ 6) 画像を安全に取り出す
                //    Clipboard.GetImage() の Image は即 Bitmap にコピーし、どちらも using で確実に解放する。
                using var src = Clipboard.GetImage();
                if (src is null) return; // 取得までの間に画像以外へ変わった

                using (var bmp = new Bitmap(src))
                {
                    // ▼ 7) 保存先ディレクトリを用意（サブフォルダはなければ作成）
                    string dir = Path.Combine(baseDir, sub);
                    Directory.CreateDirectory(dir);

                    // ▼ 8) ファイル名はミリ秒まで入れて衝突回避（yyyyMMdd_HHmmssfff.png）
                    string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmssfff}.png");

                    // ▼ 9) PNG で保存
                    bmp.Save(path, ImageFormat.Png);
                }

                // ▼ 10) 成功したら効果音
                PlayShutter();
                return; // 1回成功したら抜ける
            }
            catch
            {
                // ▼ 11) 一時的なロック/競合の可能性 → 少し待ってリトライ
                Thread.Sleep(120);
            }
        }

        // ▼ 12) 全リトライ失敗（書き込み権限なし・容量不足など）→ エラー音で知らせる
        PlayError();
    }

    static string GetActiveProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private void PlayShutter()
    {
        try
        {
            if (_shutter != null)
            {
                _shutter.Stop();   // 連続撮影で音が被らないよう一旦止める
                _shutter.Play();   // 非同期で鳴る
            }
            else SystemSounds.Asterisk.Play();        // フォールバック
        }
        catch { /* 音で失敗してもアプリは落とさない */ }
    }

    private static void PlayError()
    {
        try { SystemSounds.Hand.Play(); }
        catch { /* 音で失敗してもアプリは落とさない */ }
    }

    private void UpdateTrayUi()
    {
        string state = !_monitoringActive ? "停止中"
                     : _hotkeyRegistered ? "監視中"
                     : "監視中（ホットキー未登録）";
        string text = $"{AppInfo.DisplayName}: {state}";
        if (text.Length > 63) text = text.Substring(0, 63); // NotifyIcon.Text は63文字まで

        _tray.Icon = _monitoringActive ? _iconOn : _iconOff;
        _tray.Text = text;
        _miToggle.Text = _monitoringActive ? "監視を停止" : "監視を開始";
    }


    // class の外（名前空間直下でもOK）
    static class SingleInstance
    {
        private static System.Threading.Mutex? _mutex;
        public static bool TryLock()
        {
            bool created;
            _mutex = new System.Threading.Mutex(true, @"Global\GameshotSaver_Mutex", out created);
            return created;
        }
    }

    [STAThread]
    public static void Main()
    {
        if (!SingleInstance.TryLock()) return;  // すでに起動中なら即終了

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 想定外の例外でアプリが無言で落ちないよう、最後の砦としてメッセージを出す
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
            MessageBox.Show($"予期しないエラーが発生しました。\n{e.Exception.Message}", AppInfo.DisplayName,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new SaverApp());
    }
}
