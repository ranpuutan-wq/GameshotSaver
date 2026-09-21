// SaveApp.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GameshotSaver
{
    /// <summary>
    /// トレイ常駐アプリ本体。画像がクリップボードへ入ったら保存し、成功時にシャッター音を鳴らす。
    /// - Ctrl+Shift+V ホットキーでも手動保存可
    /// - 起動中プロセス名→サブフォルダを routes でマッピング
    /// - 稼働/停止でトレイアイコンとツールチップを更新
    /// </summary>
    internal sealed class SaveApp : Form
    {
        // ==== 設定・状態 ====
        private AppConfig _cfg = new();                               // 設定（外部保存があれば後で上書き）
        private string baseDir = "";                                  // 保存ベース
        private Dictionary<string, string> routes = new(StringComparer.OrdinalIgnoreCase); // Proc→SubFolder
        private bool _monitoringActive = true;                        // 監視ON/OFF（起動時ON）

        // ==== トレイ / メニュー ====
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _miToggle = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miSettings = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _miExit = new ToolStripMenuItem();

        // ==== サウンド ====
        private SoundPlayer _shutter;

        // ==== ホットキー ====
        private const int HOTKEY_ID = 0x1001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        // ==== WinAPI ====
        [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public SaveApp()
        {
            // フォームを見せない（完全トレイアプリ）
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0;

            // シャッター音（リソースのWAVを使う。Resources名はプロジェクトに合わせて）
            _shutter = new SoundPlayer(Properties.Resources.shutter);
            try { _shutter.Load(); } catch { /* リソースが無い/壊れてても動作継続 */ }

            // 設定ロード（外部の設定管理を使ってるならここで差し替えOK）
            LoadConfigAndApply();

            // トレイUI
            InitializeTray();
            UpdateTrayUi();

            // 監視開始（ホットキー登録＋クリップボード監視）
            UpdateMonitoring(true);

            // アプリ終了時の後始末
            Application.ApplicationExit += (_, __) =>
            {
                try { RemoveClipboardFormatListener(this.Handle); } catch { }
                try { UnregisterHotKey(this.Handle, HOTKEY_ID); } catch { }
                try { _tray?.Dispose(); } catch { }
                try { _shutter?.Dispose(); } catch { }
            };
        }

        // ===== 設定の読み込み＆反映 =====
        private void LoadConfigAndApply()
        {
            // 既存の設定ローダーがあるならそれを使う（例：_cfg = ConfigManager.LoadOrDefault();）
            // ここでは安全な既定値を設定
            if (string.IsNullOrWhiteSpace(_cfg.BaseDir))
            {
                _cfg.BaseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "ゲーム画面キャプチャ");
            }
            baseDir = _cfg.BaseDir;

            routes.Clear();
            if (_cfg.Routes != null)
            {
                foreach (var r in _cfg.Routes)
                {
                    if (!string.IsNullOrWhiteSpace(r.Process) && !string.IsNullOrWhiteSpace(r.Folder))
                        routes[r.Process.Trim()] = r.Folder.Trim();
                }
            }
            Directory.CreateDirectory(baseDir); // ベースは先に作っておく
        }

        // ===== トレイ =====
        private void InitializeTray()
        {
            // メニュー項目を設定
            _miToggle.Text = "監視を停止";
            _miToggle.Click -= OnClickToggleMonitoring; // 二重登録ガード
            _miToggle.Click += OnClickToggleMonitoring;

            _miSettings.Text = "設定...";
            _miSettings.Click -= OnClickSettings;
            _miSettings.Click += OnClickSettings;

            _miExit.Text = "終了";
            _miExit.Click -= (s, e) => Application.Exit();
            _miExit.Click += (s, e) => Application.Exit();

            _menu.Items.Clear();
            _menu.Items.AddRange(new ToolStripItem[]
            {
        _miToggle, new ToolStripSeparator(), _miSettings, new ToolStripSeparator(), _miExit
            });

            // トレイアイコンの設定
            _tray.Visible = true;
            _tray.Icon = Properties.Resources.Icon_ON;
            _tray.ContextMenuStrip = _menu;

            // 終了時に破棄
            Application.ApplicationExit -= OnAppExit;
            Application.ApplicationExit += OnAppExit;

        }

        private void OnAppExit(object? s, EventArgs e)
        {
            try { _tray.Dispose(); } catch { }
        }

        private void UpdateTrayUi()
        {
            string state = _monitoringActive ? "監視中" : "停止中";
            string text = $"{AppInfo.DisplayName} - {state}";
            if (text.Length > 63) text = text.Substring(0, 62) + "…"; // 63文字制限
            _tray.Text = text;

            _tray.Icon = _monitoringActive
                ? Properties.Resources.Icon_ON
                : Properties.Resources.ICON_OFF;

            _miToggle.Text = _monitoringActive ? "監視を停止" : "監視を開始";
        }

        private void OnClickToggleMonitoring(object? sender, EventArgs e)
        {
            UpdateMonitoring(!_monitoringActive);
        }

        private void OnClickSettings(object? sender, EventArgs e)
        {
            using var dlg = new SettingsForm(_cfg);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _cfg = dlg.Result;     // 結果を反映
                LoadConfigAndApply();  // baseDir / routes を再構築
                UpdateTrayUi();
            }
        }

        // ===== 監視ON/OFF（ホットキー＆クリップボード） =====
        private void UpdateMonitoring(bool enable)
        {
            if (enable == _monitoringActive) return;

            if (enable)
            {
                // クリップボード更新の通知
                AddClipboardFormatListener(this.Handle);
                // ホットキー登録（Ctrl+Shift+V）
                RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, (uint)Keys.V);

                _monitoringActive = true;
            }
            else
            {
                try { RemoveClipboardFormatListener(this.Handle); } catch { }
                try { UnregisterHotKey(this.Handle, HOTKEY_ID); } catch { }
                _monitoringActive = false;
            }

            UpdateTrayUi();
        }

        // ===== Win32メッセージ受信：ホットキー / クリップボード更新 =====
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                // 手動保存（プロセスマッピングが無くても保存する）
                _ = TrySaveClipboardImage(onlyWhenProcIsMapped: false);
                return;
            }
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                if (_monitoringActive)
                {
                    // 画像だった場合のみ、かつマッピングがある場合だけ保存
                    _ = TrySaveClipboardImage(onlyWhenProcIsMapped: true);
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ===== 保存本体：画像なら保存し、成功でシャッター音 =====
        private bool TrySaveClipboardImage(bool onlyWhenProcIsMapped)
        {
            try
            {
                if (!Clipboard.ContainsImage()) return false;

                using var img = Clipboard.GetImage();
                if (img == null) return false;

                // フォーカス中のプロセス名を取得
                string proc = GetActiveProcessName();
                string sub;

                if (routes.TryGetValue(proc, out var mapped))
                {
                    sub = mapped;
                }
                else
                {
                    if (onlyWhenProcIsMapped) return false;   // 監視時はマップされた時だけ保存
                    sub = string.IsNullOrEmpty(proc) ? "Unknown" : proc;
                }

                // 保存先ディレクトリとファイル名（yyyyMMdd_HHmmss_fff.png）
                string dir = Path.Combine(baseDir, sub);
                Directory.CreateDirectory(dir);
                string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
                string path = Path.Combine(dir, name);

                img.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                // 成功したのでシャッター音（被り防止で一旦Stop）
                _shutter?.Stop();
                _shutter?.Play();

                return true;
            }
            catch
            {
                // 失敗時は何も言わずスルー（ログが欲しければここに追記）
                return false;
            }
        }

        // 前面ウィンドウのプロセス名（空なら ""）
        private static string GetActiveProcessName()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return "";
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0) return "";
                using var p = Process.GetProcessById((int)pid);
                return p?.ProcessName ?? "";
            }
            catch { return ""; }
        }
    }

}
