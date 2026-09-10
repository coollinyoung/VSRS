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

            log.Dock = DockStyle.Bottom; log.Height = 155; log.Multiline = true; log.ScrollBars = ScrollBars.Both;
            log.ReadOnly = true; log.BackColor = Color.FromArgb(25, 25, 25); log.ForeColor = Color.Gainsboro;
            Controls.Add(tabs); Controls.Add(log);
            Shown += (s, e) => RefreshHardware();
        }

        private TabPage BuildVentoyTab()
        {
            var page = NewPage("1. 安裝 Ventoy 到硬碟");
            var title = AddTitle(page, "選擇 Ventoy 目標硬碟", 22);
            diskBox = AddCombo(page, 70);
            AddText(page, "會顯示磁碟編號、容量、USB/內接判斷與型號。請以容量及型號再次核對；此操作會清除整顆磁碟。", 112, Color.DarkRed);
            var refresh = AddButton(page, "重新偵測磁碟", 165, 180); refresh.Click += (s, e) => RefreshHardware();
            allowInternal = new CheckBox { Text = "允許安裝到內接/非 USB 磁碟（Ventoy /NOUSBCheck）", Left = 28, Top = 212, Width = 520, Height = 30 };
            page.Controls.Add(allowInternal);
            installButton = AddButton(page, "安裝 Ventoy（危險操作）", 255, 260); installButton.BackColor = Color.MistyRose;
            installButton.Click += async (s, e) => await InstallVentoyAsync();
            return page;
        }

        private TabPage BuildCaptureTab()
        {
            var page = NewPage("2. Windows 磁區轉 VHDX");
            AddTitle(page, "選擇包含 Windows 的來源磁區", 22);
            volumeBox = AddCombo(page, 70);
            AddText(page, "程式會優先標示含有 Windows\\System32 的磁區。WinPE 中原本的 C: 可能會變成 D: 或其他代號，請不要只看磁碟代號。", 112, Color.DarkBlue);
            AddText(page, "輸出 VHDX：", 177);
            vhdOutput = AddPathBox(page, 210, true, "VHDX 檔案|*.vhdx");
            captureButton = AddButton(page, "開始建立 VHDX", 265, 220);
            captureButton.Click += async (s, e) => await CaptureAsync();
            return page;
        }

        private TabPage BuildDifferencingTab()
        {
            var page = NewPage("3. 差分建立與合併");
            AddTitle(page, "建立差分 VHDX", 18);
            AddText(page, "父 VHDX（唯讀基底）：", 62); parentVhd = AddPathBox(page, 92, false, "VHDX 檔案|*.vhdx");
            AddText(page, "新差分 VHDX：", 137); childVhd = AddPathBox(page, 167, true, "VHDX 檔案|*.vhdx");
            createDiffButton = AddButton(page, "建立差分磁碟", 214, 200); createDiffButton.Click += async (s, e) => await CreateDiffAsync();
            AddText(page, "────────────────────────────────────────────────────────────────", 266, Color.Gray);
            AddTitle(page, "合併差分 VHDX 回上一層父磁碟", 297);
            AddText(page, "要合併的子 VHDX：", 342); mergeVhd = AddPathBox(page, 372, false, "VHDX 檔案|*.vhdx");
            AddText(page, "合併會修改父 VHDX，且不可取消。請先備份父磁碟與子磁碟。", 417, Color.DarkRed);
            mergeButton = AddButton(page, "合併到父磁碟（危險操作）", 459, 270); mergeButton.BackColor = Color.MistyRose;
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

        private static TabPage NewPage(string text) => new TabPage(text) {
            AutoScroll = true,
            AutoScrollMinSize = new Size(680, 545),
            Padding = new Padding(16),
            UseVisualStyleBackColor = true
        };

        private static Label AddTitle(Control p, string text, int y)
        {
            var c = new Label {
                Text = text, Left = 24, Top = y, Width = 850, Height = 38,
                AutoEllipsis = false, Font = new Font("Microsoft JhengHei UI", 15F, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            p.Controls.Add(c); ResizeWideControl(p, c, 24); return c;
        }

        private static Label AddText(Control p, string text, int y, Color? color = null)
        {
            var c = new Label {
                Text = text, Left = 28, Top = y, Width = 850, Height = 50,
                AutoEllipsis = false, ForeColor = color ?? Color.Black,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            p.Controls.Add(c); ResizeWideControl(p, c, 28); return c;
        }

        private static ComboBox AddCombo(Control p, int y)
        {
            var c = new ComboBox {
                Left = 28, Top = y, Width = 850, Height = 34,
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false, DropDownHeight = 240,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            p.Controls.Add(c); ResizeWideControl(p, c, 28); return c;
        }
        private static Button AddButton(Control p, string text, int y, int width) { var c = new Button { Text = text, Left = 28, Top = y, Width = width, Height = 38 }; p.Controls.Add(c); return c; }
        private static TextBox AddPathBox(Control p, int y, bool save, string filter)
        {
            var box = new TextBox { Left = 28, Top = y, Width = 750, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var button = new Button { Text = "瀏覽…", Left = 790, Top = y - 2, Width = 88, Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            button.Click += (s, e) => {
                if (save) { using (var d = new SaveFileDialog { Filter = filter, DefaultExt = "vhdx", AddExtension = true }) if (d.ShowDialog() == DialogResult.OK) box.Text = d.FileName; }
                else { using (var d = new OpenFileDialog { Filter = filter, CheckFileExists = true }) if (d.ShowDialog() == DialogResult.OK) box.Text = d.FileName; }
            };
            p.Controls.Add(box); p.Controls.Add(button);
            Action resize = () => {
                int right = Math.Max(650, p.ClientSize.Width - 28);
                button.Left = right - button.Width;
                box.Width = Math.Max(300, button.Left - box.Left - 12);
            };
            p.Resize += (s, e) => resize();
            resize();
            return box;
        }

        private static void ResizeWideControl(Control parent, Control child, int left)
        {
            Action resize = () => child.Width = Math.Max(620, parent.ClientSize.Width - left - 28);
            parent.Resize += (s, e) => resize();
            resize();
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
