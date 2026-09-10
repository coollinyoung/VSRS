using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSRS
{
    internal sealed class MainForm : Form
    {
        private readonly TabControl tabs = new TabControl();
        private readonly TextBox log = new TextBox();
        private ComboBox diskBox, volumeBox;
        private CheckBox allowInternal;
        private TextBox vhdOutput, parentVhd, childVhd, mergeVhd;
        private Button installButton, captureButton, createDiffButton, mergeButton;

        public MainForm()
        {
            Text = "VSRS - Ventoy 單機還原系統";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Width = 1040; Height = 780; MinimumSize = new Size(760, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft JhengHei UI", 10F);

            tabs.Dock = DockStyle.Fill;
            // WinPE 常使用 125%～200% DPI。固定較高的頁籤標頭，避免中文字被裁切。
            tabs.Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(220, 40);
            tabs.Padding = new Point(14, 6);
            tabs.TabPages.Add(BuildVentoyTab());
            tabs.TabPages.Add(BuildCaptureTab());
            tabs.TabPages.Add(BuildDifferencingTab());

            log.Dock = DockStyle.Bottom; log.Height = 78; log.Multiline = true; log.ScrollBars = ScrollBars.Both;
            log.ReadOnly = true; log.BackColor = Color.FromArgb(25, 25, 25); log.ForeColor = Color.Gainsboro;
            Controls.Add(tabs); Controls.Add(log);
            Shown += (s, e) => RefreshHardware();
        }

        private TabPage BuildVentoyTab()
        {
            var page = NewPage("1. 安裝 Ventoy 到硬碟");
            var content = AddContentPanel(page);
            AddTitle(content, "選擇 Ventoy 目標硬碟");
            diskBox = AddCombo(content);
            AddText(content, "會顯示磁碟編號、容量、USB/內接判斷與型號。請以容量及型號再次核對；此操作會清除整顆磁碟。", Color.DarkRed);
            var refresh = AddButton(content, "重新偵測磁碟", 180); refresh.Click += (s, e) => RefreshHardware();
            allowInternal = new CheckBox { Text = "允許安裝到內接/非 USB 磁碟（Ventoy /NOUSBCheck）", AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
            content.Controls.Add(allowInternal);
            installButton = AddButton(content, "安裝 Ventoy（危險操作）", 260); installButton.BackColor = Color.MistyRose;
            installButton.Click += async (s, e) => await InstallVentoyAsync();
            return page;
        }

        private TabPage BuildCaptureTab()
        {
            var page = NewPage("2. Windows 磁區轉 VHDX");
            var content = AddContentPanel(page);
            AddTitle(content, "選擇包含 Windows 的來源磁區");
            volumeBox = AddCombo(content);
            AddText(content, "程式會優先標示含有 Windows\\System32 的磁區。WinPE 中原本的 C: 可能會變成 D: 或其他代號，請不要只看磁碟代號。", Color.DarkBlue);
            AddText(content, "輸出 VHDX：");
            vhdOutput = AddPathBox(content, true, "VHDX 檔案|*.vhdx");
            captureButton = AddButton(content, "開始建立 VHDX", 220);
            captureButton.Click += async (s, e) => await CaptureAsync();
            return page;
        }

        private TabPage BuildDifferencingTab()
        {
            var page = NewPage("3. 差分建立與合併");
            var content = AddContentPanel(page);
            AddTitle(content, "建立差分 VHDX");
            AddText(content, "父 VHDX（唯讀基底）："); parentVhd = AddPathBox(content, false, "VHDX 檔案|*.vhdx");
            AddText(content, "新差分 VHDX："); childVhd = AddPathBox(content, true, "VHDX 檔案|*.vhdx");
            createDiffButton = AddButton(content, "建立差分磁碟", 200); createDiffButton.Click += async (s, e) => await CreateDiffAsync();
            AddSeparator(content);
            AddTitle(content, "合併差分 VHDX 回上一層父磁碟");
            AddText(content, "要合併的子 VHDX："); mergeVhd = AddPathBox(content, false, "VHDX 檔案|*.vhdx");
            AddText(content, "合併會修改父 VHDX，且不可取消。請先備份父磁碟與子磁碟。", Color.DarkRed);
            mergeButton = AddButton(content, "合併到父磁碟（危險操作）", 270); mergeButton.BackColor = Color.MistyRose;
            mergeButton.Click += async (s, e) => await MergeAsync();
            return page;
        }

        private async Task InstallVentoyAsync()
        {
            var disk = diskBox.SelectedItem as DiskInfo;
            if (disk == null) { Warn("請先選擇目標硬碟。"); return; }
            if (disk.IsBootOrSystem) { Warn("此磁碟被判定為目前系統/開機磁碟，VSRS 已禁止操作。"); return; }
            if (!disk.IsUsb && !allowInternal.Checked) { Warn("目標被判定為內接/非 USB 磁碟。若確實要安裝，請先勾選允許選項。"); return; }
            string ventoy = ToolLocator.Find("Ventoy2Disk.exe");
            if (ventoy == null) { Warn("找不到 Ventoy2Disk.exe。請放入程式旁的 Tools\\Ventoy 資料夾。"); return; }
            string typed = Prompt.Show($"即將清除：磁碟 {disk.Number} / {disk.Model} / {disk.SizeText}\r\n請輸入磁碟編號 {disk.Number} 才能繼續：", "最後確認");
            if (typed != disk.Number.ToString()) { WriteLog("使用者取消：確認編號不符。"); return; }
            if (MessageBox.Show("整顆目標磁碟的資料都會消失。確定安裝？", "不可逆警告", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string args = $"VTOYCLI /I /PhyDrive:{disk.Number}" + (!disk.IsUsb ? " /NOUSBCheck" : "");
            await RunBusyAsync(() => ProcessService.RunAsync(ventoy, args, WriteLog));
        }

        private async Task CaptureAsync()
        {
            var volume = volumeBox.SelectedItem as VolumeInfo;
            if (volume == null || string.IsNullOrWhiteSpace(vhdOutput.Text)) { Warn("請選擇來源磁區及輸出檔案。"); return; }
            if (!volume.HasWindows && MessageBox.Show("此磁區未偵測到 Windows\\System32，仍要繼續？", "確認來源", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string disk2vhd = ToolLocator.Find("disk2vhd.exe");
            if (disk2vhd == null) { Warn("找不到 disk2vhd.exe。請從 Microsoft Sysinternals 下載後放入 Tools 資料夾。"); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(vhdOutput.Text));
            await RunBusyAsync(() => ProcessService.RunAsync(disk2vhd, $"-accepteula {volume.DriveLetter} \"{vhdOutput.Text}\"", WriteLog));
        }

        private async Task CreateDiffAsync()
        {
            if (!File.Exists(parentVhd.Text) || string.IsNullOrWhiteSpace(childVhd.Text)) { Warn("請選擇存在的父 VHDX 與新子 VHDX 路徑。"); return; }
            if (File.Exists(childVhd.Text)) { Warn("子 VHDX 已存在，為避免覆寫，請選擇新檔名。"); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(childVhd.Text));
            await RunBusyAsync(() => ProcessService.RunDiskPartAsync(new[] { $"create vdisk file=\"{childVhd.Text}\" parent=\"{parentVhd.Text}\"", "exit" }, WriteLog));
        }

        private async Task MergeAsync()
        {
            if (!File.Exists(mergeVhd.Text)) { Warn("請選擇要合併的子 VHDX。"); return; }
            if (MessageBox.Show("這會把子磁碟變更寫回父 VHDX。已完成備份並確定繼續？", "合併確認", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await RunBusyAsync(() => ProcessService.RunDiskPartAsync(new[] { $"select vdisk file=\"{mergeVhd.Text}\"", "merge vdisk depth=1", "exit" }, WriteLog));
        }

        private void RefreshHardware()
        {
            try
            {
                diskBox.Items.Clear(); foreach (var d in HardwareService.GetDisks()) diskBox.Items.Add(d); if (diskBox.Items.Count > 0) diskBox.SelectedIndex = 0;
                volumeBox.Items.Clear(); foreach (var v in HardwareService.GetVolumes()) volumeBox.Items.Add(v); if (volumeBox.Items.Count > 0) volumeBox.SelectedIndex = 0;
                WriteLog($"偵測完成：{diskBox.Items.Count} 顆磁碟，{volumeBox.Items.Count} 個本機磁區。");
            }
            catch (Exception ex) { Warn("硬體偵測失敗。USBOX 必須包含 WMI 元件。\r\n" + ex.Message); }
        }

        private async Task RunBusyAsync(Func<Task<CommandResult>> action)
        {
            SetBusy(true); WriteLog("開始執行……");
            try { var r = await action(); WriteLog(r.Success ? "完成（ExitCode 0）。" : "執行失敗，ExitCode=" + r.ExitCode); if (!r.Success) Warn("作業未成功，請查看下方紀錄。"); }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy) { installButton.Enabled = captureButton.Enabled = createDiffButton.Enabled = mergeButton.Enabled = !busy; UseWaitCursor = busy; }
        private void WriteLog(string text) { if (InvokeRequired) { BeginInvoke(new Action<string>(WriteLog), text); return; } log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); }
        private static void Warn(string text) => MessageBox.Show(text, "VSRS", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private static TabPage NewPage(string text) => new TabPage(text) { Padding = new Padding(0), UseVisualStyleBackColor = true };

        private static FlowLayoutPanel AddContentPanel(TabPage page)
        {
            var panel = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(28, 22, 28, 22)
            };
            page.Controls.Add(panel);
            panel.ClientSizeChanged += (s, e) => ResizeFlowChildren(panel);
            return panel;
        }

        private static Label AddTitle(FlowLayoutPanel p, string text)
        {
            var c = new Label {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft JhengHei UI", 15F, FontStyle.Bold),
                Margin = new Padding(3, 4, 3, 14)
            };
            p.Controls.Add(c);
            return c;
        }

        private static Label AddText(FlowLayoutPanel p, string text, Color? color = null)
        {
            var c = new Label {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                ForeColor = color ?? Color.Black,
                Margin = new Padding(3, 5, 3, 10)
            };
            p.Controls.Add(c);
            ResizeFlowChildren(p);
            return c;
        }

        private static ComboBox AddCombo(FlowLayoutPanel p)
        {
            var c = new ComboBox {
                Width = 850,
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                DropDownHeight = 240,
                Margin = new Padding(3, 0, 3, 12)
            };
            p.Controls.Add(c);
            ResizeFlowChildren(p);
            return c;
        }

        private static Button AddButton(FlowLayoutPanel p, string text, int width)
        {
            var c = new Button {
                Text = text,
                Width = width,
                Height = Math.Max(44, p.Font.Height + 24),
                Margin = new Padding(3, 5, 3, 14),
                AutoSize = false,
                UseCompatibleTextRendering = true
            };
            p.Controls.Add(c);
            return c;
        }

        private static TextBox AddPathBox(FlowLayoutPanel p, bool save, string filter)
        {
            var row = new TableLayoutPanel {
                Width = 850,
                Height = Math.Max(44, p.Font.Height + 24),
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(3, 0, 3, 12)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            var box = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 12, 5) };
            var button = new Button { Text = "瀏覽…", Dock = DockStyle.Fill, Margin = new Padding(0), AutoSize = false, UseCompatibleTextRendering = true };
            button.Click += (s, e) => {
                if (save) { using (var d = new SaveFileDialog { Filter = filter, DefaultExt = "vhdx", AddExtension = true }) if (d.ShowDialog() == DialogResult.OK) box.Text = d.FileName; }
                else { using (var d = new OpenFileDialog { Filter = filter, CheckFileExists = true }) if (d.ShowDialog() == DialogResult.OK) box.Text = d.FileName; }
            };
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(button, 1, 0);
            p.Controls.Add(row);
            ResizeFlowChildren(p);
            return box;
        }

        private static void AddSeparator(FlowLayoutPanel p)
        {
            var line = new Panel { Height = 1, Width = 850, BackColor = Color.Silver, Margin = new Padding(3, 10, 3, 18) };
            p.Controls.Add(line);
            ResizeFlowChildren(p);
        }

        private static void ResizeFlowChildren(FlowLayoutPanel p)
        {
            int width = Math.Max(560, p.ClientSize.Width - p.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control c in p.Controls)
            {
                if (c is ComboBox || c is TableLayoutPanel || (c is Panel && !(c is FlowLayoutPanel))) c.Width = width;
                var label = c as Label;
                if (label != null) label.MaximumSize = new Size(width, 0);
            }
        }
    }

    internal static class Prompt
    {
        public static string Show(string text, string caption)
        {
            using (var f = new Form { Text = caption, Width = 540, Height = 210, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Text = text, Left = 18, Top = 18, Width = 485, Height = 72 };
                var input = new TextBox { Left = 18, Top = 94, Width = 485 };
                var ok = new Button { Text = "確認", Left = 330, Top = 128, Width = 82, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 421, Top = 128, Width = 82, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { label, input, ok, cancel }); f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
            }
        }
    }
}
