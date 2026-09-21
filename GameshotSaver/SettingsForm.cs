using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

/// <summary>
/// 設定フォーム：
/// - BaseDir（保存ベースフォルダ）
/// - routes（Process → 保存サブフォルダ）
/// をGUIで編集する。  
/// Process/FOLDER列はコンボボックス化し、候補を（実行中プロセス／既存フォルダ／既存設定）から生成。
/// 自由入力も可能で、未登録値は確定時に候補へ自動追加する。
/// </summary>
public class SettingsForm : Form
{
    // === モデル ===
    private readonly AppConfig _work;                 // 編集用コピー（OKで呼び出し元へ返す）
    private readonly BindingList<RouteItem> _binding; // DataGridViewのデータソース

    // === 候補データ ===
    private List<string> _procCandidates = new();     // プロセス候補（実行中＋既存設定＋定番）
    private List<string> _folderCandidates = new();   // フォルダ候補（BaseDir直下＋既存設定＋定番）

    // === UI部品（動的レイアウト前提） ===
    private readonly TextBox txtBaseDir = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
    private readonly Button btnBrowse = new() { Text = "参照...", Width = 80 };
    private readonly DataGridView dgv = new() { Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
    private readonly Button btnAdd = new() { Text = "行追加" };
    private readonly Button btnDel = new() { Text = "選択削除" };
    private readonly Button btnOk = new() { Text = "OK", DialogResult = DialogResult.OK };
    private readonly Button btnCancel = new() { Text = "キャンセル", DialogResult = DialogResult.Cancel };
    private readonly Button btnRefresh = new() { Text = "候補更新" }; // プロセス/フォルダ候補を再生成

    // DataGridView列（候補の差し替えで使うため参照保持）
    private DataGridViewComboBoxColumn _colProcess;
    private DataGridViewComboBoxColumn _colFolder;

    /// <summary>呼び出し側が受け取る編集結果（OK時に送出）</summary>
    public AppConfig Result => _work;

    public SettingsForm(AppConfig src)
    {
        // ===== フォーム基本 =====
        Text = $"{GameshotSaver.AppInfo.DisplayName} 設定";
        Width = 720; Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        // ===== モデルを編集用にディープコピー（元データを汚さない）=====
        _work = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(
                    System.Text.Json.JsonSerializer.Serialize(src))!;
        _binding = new BindingList<RouteItem>(_work.Routes);

       
        // ===== 上部：BaseDir行 =====
        var lblBase = new Label { Text = "保存ベースフォルダ：", AutoSize = true, Top = 16, Left = 12 };

        txtBaseDir.Top = 12; txtBaseDir.Left = 150; txtBaseDir.Width = ClientSize.Width - 150 - 100;
        txtBaseDir.Text = _work.BaseDir;
        txtBaseDir.TextChanged += (s, e) => RefreshFolderCandidates(); // BaseDir変更でフォルダ候補を即更新

        btnBrowse.Top = 10; btnBrowse.Left = txtBaseDir.Right + 8; btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowse.Click += (s, e) =>
        {
            using var fb = new FolderBrowserDialog();
            fb.SelectedPath = Directory.Exists(txtBaseDir.Text) ? txtBaseDir.Text : _work.BaseDir;
            if (fb.ShowDialog(this) == DialogResult.OK)
                txtBaseDir.Text = fb.SelectedPath; // TextChangedで候補更新が走る
        };

        // ===== 中段：routes編集テーブル =====
        dgv.Top = 52; dgv.Left = 12; dgv.Width = ClientSize.Width - 24; dgv.Height = ClientSize.Height - 120;
        dgv.AllowUserToAddRows = false;             // 行追加はボタンで行う
        dgv.AllowUserToDeleteRows = true;
        dgv.AutoGenerateColumns = false;
        dgv.DataSource = _binding;
        dgv.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        dgv.MultiSelect = true;

        // 列：Process（コンボボックス＋自由入力可）
        _colProcess = new DataGridViewComboBoxColumn
        {
            HeaderText = "プロセス名 (例: GenshinImpact)",
            DataPropertyName = nameof(RouteItem.Process),
            DataSource = _procCandidates,           // 初期は空→後でRefreshで詰める
            FlatStyle = FlatStyle.Standard,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 55
        };
        // 列：Folder（コンボボックス＋自由入力可）
        _colFolder = new DataGridViewComboBoxColumn
        {
            HeaderText = "保存サブフォルダ (例: Genshin)",
            DataPropertyName = nameof(RouteItem.Folder),
            DataSource = _folderCandidates,         // 初期は空→後でRefreshで詰める
            FlatStyle = FlatStyle.Standard,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 45
        };
        dgv.Columns.AddRange(_colProcess, _colFolder);

        // 編集コントロールの体験向上（オートコンプリート＋自由入力）
        dgv.EditingControlShowing += (s, e) =>
        {
            if (e.Control is DataGridViewComboBoxEditingControl cb)
            {
                cb.DropDownStyle = ComboBoxStyle.DropDown;     // テキスト入力も可
                cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                cb.AutoCompleteSource = AutoCompleteSource.ListItems;
            }
        };

        // 値確定時：自由入力が候補に無ければ自動追加／Folderは簡易バリデーション
        dgv.CellValidating += (s, e) =>
        {
            var col = dgv.Columns[e.ColumnIndex];
            var val = (e.FormattedValue ?? "").ToString()!.Trim();
            if (val.Length == 0) return;

            if (col == _colProcess)
            {
                if (!_procCandidates.Contains(val, StringComparer.OrdinalIgnoreCase))
                {
                    _procCandidates.Add(val);
                    _procCandidates.Sort(StringComparer.OrdinalIgnoreCase);
                    _colProcess.DataSource = null;              // いったん外して
                    _colProcess.DataSource = _procCandidates;   // 再バインド
                }
            }
            else if (col == _colFolder)
            {
                // Windowsで使えない文字を弾く
                if (val.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(this, "フォルダ名に使えない文字が含まれています。", "入力エラー",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
                if (!_folderCandidates.Contains(val, StringComparer.OrdinalIgnoreCase))
                {
                    _folderCandidates.Add(val);
                    _folderCandidates.Sort(StringComparer.OrdinalIgnoreCase);
                    _colFolder.DataSource = null;
                    _colFolder.DataSource = _folderCandidates;
                }
            }
        };

        // ===== 下段：操作ボタン =====
        btnAdd.Top = dgv.Bottom + 8; btnAdd.Left = 12;
        btnAdd.Click += (s, e) => _binding.Add(new RouteItem());  // 空行を追加

        btnDel.Top = dgv.Bottom + 8; btnDel.Left = btnAdd.Right + 8;
        btnDel.Click += (s, e) =>
        {
            foreach (DataGridViewRow row in dgv.SelectedRows)
                if (row.DataBoundItem is RouteItem it) _binding.Remove(it);
        };

        btnRefresh.Top = dgv.Bottom + 8; btnRefresh.Left = btnDel.Right + 8;
        btnRefresh.Click += (s, e) => { RefreshProcessCandidates(); RefreshFolderCandidates(); };

        btnOk.Top = dgv.Bottom + 8; btnOk.Left = ClientSize.Width - 180; btnOk.Width = 80; btnOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        btnCancel.Top = dgv.Bottom + 8; btnCancel.Left = ClientSize.Width - 90; btnCancel.Width = 80; btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        // OK：UI→_work へ反映（空行除去・Trimはここで最低限、保存時もNormalizeされる）
        btnOk.Click += (s, e) =>
        {
            _work.BaseDir = txtBaseDir.Text.Trim();
            _work.Routes = _binding
                .Where(x => !string.IsNullOrWhiteSpace(x.Process) && !string.IsNullOrWhiteSpace(x.Folder))
                .Select(x => new RouteItem { Process = x.Process.Trim(), Folder = x.Folder.Trim() })
                .ToList();
        };

        Controls.AddRange(new Control[] { lblBase, txtBaseDir, btnBrowse, dgv, btnAdd, btnDel, btnRefresh, btnOk, btnCancel });

        // ===== レイアウト追従（リサイズ時に各幅/位置を調整）=====
        Resize += (s, e) =>
        {
            txtBaseDir.Width = ClientSize.Width - 150 - 100;
            dgv.Width = ClientSize.Width - 24;
            dgv.Height = ClientSize.Height - 120;
            btnOk.Left = ClientSize.Width - 180;
            btnCancel.Left = ClientSize.Width - 90;
        };

        // ===== 初期候補の生成 =====
        RefreshProcessCandidates(); // 実行中プロセス＋既存設定＋定番
        RefreshFolderCandidates();  // BaseDir配下のフォルダ＋既存設定＋定番
    }

    /// <summary>
    /// プロセス候補を再生成：
    /// - 既存routes
    /// - 実行中プロセス（Process.GetProcesses）
    /// - よく使う定番
    /// をマージして重複排除し、コンボボックスへ反映。
    /// </summary>
    private void RefreshProcessCandidates()
    {
        var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in _work.Routes)
            if (!string.IsNullOrWhiteSpace(r.Process)) hs.Add(r.Process.Trim());

        foreach (var p in Process.GetProcesses())
        {
            try { if (!string.IsNullOrWhiteSpace(p.ProcessName)) hs.Add(p.ProcessName.Trim()); }
            catch { /* アクセス不可プロセスは無視 */ }
        }

        string[] wellKnown = { "GenshinImpact", "YuanShen", "StarRail", "msedge", "chrome", "steam", "obs64" };
        foreach (var n in wellKnown) hs.Add(n);

        _procCandidates = hs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        _colProcess.DataSource = null;
        _colProcess.DataSource = _procCandidates;
    }

    /// <summary>
    /// フォルダ候補を再生成：
    /// - 既存routesにあるフォルダ名
    /// - BaseDir直下の実在フォルダ
    /// - よく使う定番
    /// をマージして重複排除し、コンボボックスへ反映。
    /// </summary>
    private void RefreshFolderCandidates()
    {
        var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in _work.Routes)
            if (!string.IsNullOrWhiteSpace(r.Folder)) hs.Add(r.Folder.Trim());

        var baseDir = txtBaseDir.Text.Trim();
        try
        {
            if (Directory.Exists(baseDir))
            {
                foreach (var d in Directory.GetDirectories(baseDir))
                {
                    var name = Path.GetFileName(d);
                    if (!string.IsNullOrEmpty(name)) hs.Add(name);
                }
            }
        }
        catch { /* アクセス不可は無視 */ }

        string[] wellKnown = { "Genshin", "StarRail", "Web", "Screenshots", "Temp" };
        foreach (var n in wellKnown) hs.Add(n);

        _folderCandidates = hs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        _colFolder.DataSource = null;
        _colFolder.DataSource = _folderCandidates;
    }
}
