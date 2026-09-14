using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSRS
{
    internal sealed class MainForm : Form
    {
        private static readonly Color WindowBackColor = Color.FromArgb(238, 243, 248);
        private static readonly Color ContentBackColor = Color.FromArgb(248, 250, 252);
        private static readonly Color TabDarkColor = Color.FromArgb(38, 52, 69);
        private static readonly Color AccentColor = Color.FromArgb(36, 99, 155);
        private static readonly Color TextColor = Color.FromArgb(31, 45, 61);
        private static readonly Cursor ActionCursor = CreateYellowHandCursor();
        private readonly TabControl tabs = new TabControl();
        private readonly TextBox log = new TextBox();
        private ComboBox diskBox, volumeBox;
        private CheckBox allowInternal;
        private TextBox vhdOutput, parentVhd, diffOutputFolder, mergeVhd, copySourceFolder;
        private ComboBox copyTargetVolume;
        private Button installButton, captureButton, createDiffButton, mergeButton, copyButton;

        public MainForm()
        {
            Text = "VSRS - Ventoy 單機還原系統";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Width = 1040; Height = 780; MinimumSize = new Size(760, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft JhengHei UI", 10F);
            BackColor = WindowBackColor;
            DoubleBuffered = true;

            tabs.Dock = DockStyle.Fill;
            // WinPE 常使用 125%～200% DPI。固定較高的頁籤標頭，避免中文字被裁切。
            tabs.Font = new Font("Microsoft JhengHei UI", 10.5F, FontStyle.Bold);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(205, 40);
            tabs.Padding = new Point(14, 6);
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.Cursor = ActionCursor;
            tabs.DrawItem += DrawTabItem;
            tabs.SelectedIndexChanged += (s, e) => tabs.Invalidate();
            tabs.TabPages.Add(BuildVentoyTab());
            tabs.TabPages.Add(BuildCaptureTab());
            tabs.TabPages.Add(BuildDifferencingTab());
            tabs.TabPages.Add(BuildCopyTab());

            log.Dock = DockStyle.Bottom; log.Height = 78; log.Multiline = true; log.ScrollBars = ScrollBars.Both;
            log.ReadOnly = true; log.BackColor = Color.FromArgb(24, 32, 42); log.ForeColor = Color.FromArgb(212, 223, 234);
            log.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(tabs); Controls.Add(log);
            Shown += (s, e) => RefreshHardware();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                int enabled = 1;
                // Windows 10 20H1+ 使用 20；較舊的 Windows 10 使用 19。
                if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));

                // Windows 11 支援自訂標題列、文字與邊框色；舊版系統會安全地忽略。
                int caption = ToColorRef(TabDarkColor);
                int text = ToColorRef(Color.White);
                int border = ToColorRef(Color.FromArgb(29, 79, 124));
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        private TabPage BuildVentoyTab()
        {
            var page = NewPage("1. 安裝 Ventoy 到硬碟");
            var content = AddContentPanel(page);
            AddTitle(content, "選擇 Ventoy 目標硬碟");
            diskBox = AddCombo(content);
            AddText(content, "會顯示磁碟編號、容量、USB/內接判斷與型號。請以容量及型號再次核對；此操作會清除整顆磁碟。", Color.DarkRed);
            var refresh = AddButton(content, "重新偵測磁碟", 180); refresh.Click += (s, e) => RefreshHardware();
            allowInternal = new CheckBox { Text = "允許安裝到內接/非 USB 磁碟（GPT、NTFS、NOUSBCheck）", AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
            content.Controls.Add(allowInternal);
            installButton = AddButton(content, "安裝 Ventoy（危險操作）", 260); StyleDangerButton(installButton);
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
            AddText(content, "差分檔存放資料夾（固定建立 temp.vhdx 與 temp2.vhdx）：");
            diffOutputFolder = AddFolderPathBox(content);
            AddText(content, "建立順序：基底 VHDX → temp.vhdx → temp2.vhdx。請只選資料夾，不需要輸入檔名。", Color.FromArgb(36, 83, 125));
            createDiffButton = AddButton(content, "建立兩個差分磁碟", 220); createDiffButton.Click += async (s, e) => await CreateDiffAsync();
            AddSeparator(content);
            AddTitle(content, "合併差分 VHDX 回上一層父磁碟");
            AddText(content, "要合併的子 VHDX："); mergeVhd = AddPathBox(content, false, "VHDX 檔案|*.vhdx");
            AddText(content, "合併會修改父 VHDX，且不可取消。請先備份父磁碟與子磁碟。", Color.DarkRed);
            mergeButton = AddButton(content, "合併到父磁碟（危險操作）", 270); StyleDangerButton(mergeButton);
            mergeButton.Click += async (s, e) => await MergeAsync();
            return page;
        }

        private TabPage BuildCopyTab()
        {
            var page = NewPage("4. 複製 VentoyHDD 資料");
            var content = AddContentPanel(page);
            AddTitle(content, "複製隨身碟中的 VentoyHDD 資料");
            AddText(content, "來源為 USB 隨身碟根目錄下的 ventoyhdd 資料夾。程式會複製其中所有檔案及子資料夾，並保持原本目錄結構。", Color.FromArgb(36, 83, 125));
            AddText(content, "來源資料夾：");
            copySourceFolder = AddFolderPathBox(content);
            var detect = AddButton(content, "自動搜尋隨身碟", 200);
            detect.Click += (s, e) => DetectVentoyHddSource(true);
            AddText(content, "目的磁區（Ventoy 內接磁碟根目錄）：");
            copyTargetVolume = AddCombo(content);
            AddText(content, "安全限制：不顯示 USB 磁區及偵測到 Windows 的磁區。同名檔案將會覆蓋，來源資料不會刪除。", Color.DarkRed);
            copyButton = AddButton(content, "開始複製資料", 220);
            copyButton.Click += async (s, e) => await CopyVentoyDataAsync();
            return page;
        }

        private async Task InstallVentoyAsync()
        {
            var disk = diskBox.SelectedItem as DiskInfo;
            if (disk == null) { Warn("請先選擇目標硬碟。"); return; }
            if (disk.IsBootOrSystem) WriteLog("高風險警告：目標磁碟包含 Windows 資料夾，但已允許繼續安裝。");
            if (!disk.IsUsb && !allowInternal.Checked) { Warn("目標被判定為內接/非 USB 磁碟。若確實要安裝，請先勾選允許選項。"); return; }
            string ventoy = ToolLocator.Find("Ventoy2Disk_X64.exe");
            if (ventoy == null) { Warn("找不到 Ventoy2Disk_X64.exe。請確認它與 Ventoy2Disk.exe 位於程式旁的 Tools\\Ventoy 同一層資料夾。"); return; }
            string typed = Prompt.Show($"即將清除：磁碟 {disk.Number} / {disk.Model} / {disk.SizeText}\r\n請輸入磁碟編號 {disk.Number} 才能繼續：", "最後確認");
            if (typed != disk.Number.ToString()) { WriteLog("使用者取消：確認編號不符。"); return; }
            if (MessageBox.Show("整顆目標磁碟的資料都會消失。確定安裝？", "不可逆警告", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string args = $"VTOYCLI /I /PhyDrive:{disk.Number}" + (!disk.IsUsb ? " /GPT /FS:NTFS /NOUSBCheck" : "");
            WriteLog("Ventoy 安裝參數：" + args);
            await RunBusyAsync(() => ProcessService.RunAsync(ventoy, args, WriteLog));
        }

        private async Task CaptureAsync()
        {
            var volume = volumeBox.SelectedItem as VolumeInfo;
            if (volume == null || string.IsNullOrWhiteSpace(vhdOutput.Text)) { Warn("請選擇來源磁區及輸出檔案。"); return; }

            string disk2vhd = ToolLocator.Find("disk2vhd64.exe");
            if (disk2vhd == null) { Warn("找不到 disk2vhd64.exe。x64 WinPE 必須使用 64 位元版本，請從 Microsoft Sysinternals 下載後放入 Tools 資料夾。"); return; }

            string output;
            try { output = Path.GetFullPath(vhdOutput.Text.Trim()); }
            catch (Exception ex) { Warn("VHDX 輸出路徑無效。\r\n" + ex.Message); return; }
            if (!string.Equals(Path.GetExtension(output), ".vhdx", StringComparison.OrdinalIgnoreCase))
            {
                Warn("輸出檔案必須使用 .vhdx 副檔名。"); return;
            }

            string outputDirectory = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(outputDirectory)) { Warn("請指定完整的 VHDX 輸出資料夾及檔名。"); return; }
            Directory.CreateDirectory(outputDirectory);
            vhdOutput.Text = output;

            // Disk2vhd 不接受 -accepteula 命令列參數；改以 Sysinternals 標準登錄值接受 EULA。
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Sysinternals\Disk2vhd"))
                    key?.SetValue("EulaAccepted", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { WriteLog("無法預先寫入 Disk2vhd EULA 登錄值：" + ex.Message); }

            // WinPE 沒有 VSS，使用 -c 直接複製，避免 Volume Shadow Copy 失敗。
            // 語法：disk2vhd64.exe -c <來源磁區> <VHDX 檔案>。
            string arguments = $"-c {volume.DriveLetter} \"{output}\"";
            WriteLog($"VHDX 來源磁區：{volume.DriveLetter}（Windows={volume.HasWindows}）");
            WriteLog("實際執行檔：" + disk2vhd);
            WriteLog("實際參數：" + arguments);
            await RunBusyAsync(() => ProcessService.RunAsync(disk2vhd, arguments, WriteLog));
        }

        private static bool TryNormalizeVhdxPath(string input, bool mustExist, out string path, out string error)
        {
            path = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "尚未選擇 VHDX 檔案。";
                return false;
            }

            try { path = Path.GetFullPath(input.Trim()); }
            catch (Exception ex)
            {
                error = "VHDX 路徑無效。\r\n" + ex.Message;
                return false;
            }

            if (!string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase))
            {
                error = "檔案必須使用 .vhdx 副檔名。";
                return false;
            }
            if (mustExist && !File.Exists(path))
            {
                error = "找不到 VHDX 檔案：\r\n" + path;
                return false;
            }
            return true;
        }

        private async Task CreateDiffAsync()
        {
            if (!TryNormalizeVhdxPath(parentVhd.Text, true, out string parent, out string error))
            {
                Warn("父 VHDX：\r\n" + error); return;
            }

            string outputDirectory;
            try
            {
                if (string.IsNullOrWhiteSpace(diffOutputFolder.Text))
                {
                    Warn("請選擇 temp.vhdx 與 temp2.vhdx 的存放資料夾。"); return;
                }
                outputDirectory = Path.GetFullPath(diffOutputFolder.Text.Trim());
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex)
            {
                Warn("差分檔存放資料夾無效或無法建立。\r\n" + ex.Message); return;
            }

            string tempVhd = Path.Combine(outputDirectory, "temp.vhdx");
            string temp2Vhd = Path.Combine(outputDirectory, "temp2.vhdx");
            if (string.Equals(parent, tempVhd, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parent, temp2Vhd, StringComparison.OrdinalIgnoreCase))
            {
                Warn("基底 VHDX 不可命名為指定資料夾中的 temp.vhdx 或 temp2.vhdx。"); return;
            }
            if (File.Exists(tempVhd) || File.Exists(temp2Vhd))
            {
                string existing = string.Join("\r\n", new[] { tempVhd, temp2Vhd }.Where(File.Exists));
                Warn("為避免覆寫，建立作業已停止。請先移走或刪除下列既有檔案：\r\n" + existing); return;
            }

            parentVhd.Text = parent;
            diffOutputFolder.Text = outputDirectory;
            WriteLog("差分基底檔：" + parent);
            WriteLog("第一層差分：" + tempVhd);
            WriteLog("第二層差分：" + temp2Vhd);

            await RunBusyAsync(async () => {
                CommandResult result = await ProcessService.RunDiskPartAsync(new[] {
                    $"create vdisk file=\"{tempVhd}\" parent=\"{parent}\"",
                    $"create vdisk file=\"{temp2Vhd}\" parent=\"{tempVhd}\"",
                    "exit"
                }, WriteLog);

                bool tempCreated = File.Exists(tempVhd);
                bool temp2Created = File.Exists(temp2Vhd);
                WriteLog("檔案確認 temp.vhdx：" + (tempCreated ? "已建立" : "未建立"));
                WriteLog("檔案確認 temp2.vhdx：" + (temp2Created ? "已建立" : "未建立"));

                if (!result.Success || !tempCreated || !temp2Created)
                {
                    string missing = !tempCreated && !temp2Created ? "temp.vhdx、temp2.vhdx" :
                                     !tempCreated ? "temp.vhdx" : "temp2.vhdx";
                    return new CommandResult {
                        ExitCode = result.Success ? -2 : result.ExitCode,
                        Output = result.Output + Environment.NewLine +
                                 "建立結果驗證失敗，指定資料夾中缺少：" + missing
                    };
                }

                return result;
            });
        }

        private async Task MergeAsync()
        {
            if (!TryNormalizeVhdxPath(mergeVhd.Text, true, out string child, out string error))
            {
                Warn("要合併的子 VHDX：\r\n" + error); return;
            }
            mergeVhd.Text = child;
            if (MessageBox.Show(
                "這會把子磁碟變更寫回直接父層 VHDX，並可能使同一父檔的其他差分磁碟失效。\r\n\r\n請先關閉使用此 VHDX 的程式並備份父、子檔案。確定繼續？",
                "合併確認", MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            WriteLog("準備合併子 VHDX：" + child);
            await RunBusyAsync(() => ProcessService.RunDiskPartAsync(new[] {
                $"select vdisk file=\"{child}\"",
                "detach vdisk noerr",
                "merge vdisk depth=1",
                "exit"
            }, WriteLog));
        }

        private async Task CopyVentoyDataAsync()
        {
            string source = copySourceFolder.Text.Trim();
            var target = copyTargetVolume.SelectedItem as VolumeInfo;
            if (!Directory.Exists(source)) { Warn("找不到來源 ventoyhdd 資料夾，請重新搜尋或手動選擇。"); return; }
            if (!string.Equals(new DirectoryInfo(source).Name, "ventoyhdd", StringComparison.OrdinalIgnoreCase))
            {
                Warn("來源資料夾名稱必須是 ventoyhdd。"); return;
            }
            if (target == null) { Warn("請選擇 Ventoy 內接磁碟的目的磁區。"); return; }
            if (target.IsUsb || target.HasWindows) { Warn("安全檢查未通過：目的地不可為 USB 或 Windows 系統磁區。"); return; }

            string destination = Path.GetPathRoot(target.DriveLetter + "\\");
            string sourceRoot = Path.GetFullPath(source).TrimEnd('\\') + "\\";
            if (sourceRoot.StartsWith(destination, StringComparison.OrdinalIgnoreCase))
            {
                Warn("來源與目的地不可位於同一個目的磁區。"); return;
            }
            if (MessageBox.Show($"來源：{sourceRoot}\r\n目的：{destination}\r\n\r\n將保留目錄結構，並覆蓋目的地的同名檔案。確定開始？",
                "確認複製 VentoyHDD 資料", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            await RunBusyAsync(async () => {
                int files = 0;
                try
                {
                    await Task.Run(() => {
                        foreach (string directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
                        {
                            string relative = directory.Substring(sourceRoot.Length);
                            Directory.CreateDirectory(Path.Combine(destination, relative));
                        }
                        foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
                        {
                            string relative = file.Substring(sourceRoot.Length);
                            string output = Path.Combine(destination, relative);
                            string outputDirectory = Path.GetDirectoryName(output);
                            if (!Directory.Exists(outputDirectory)) Directory.CreateDirectory(outputDirectory);
                            File.Copy(file, output, true);
                            files++;
                            WriteLog("已複製：" + relative);
                        }
                    });
                    return new CommandResult { ExitCode = 0, Output = $"完成，共複製 {files} 個檔案。" };
                }
                catch (Exception ex)
                {
                    WriteLog("複製失敗：" + ex.Message);
                    return new CommandResult { ExitCode = -1, Output = ex.ToString() };
                }
            });
        }

        private void DetectVentoyHddSource(bool showResult)
        {
            string found = null;
            var usbLetters = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var volume in HardwareService.GetVolumes().Where(v => v.IsUsb)) usbLetters.Add(volume.DriveLetter);
            }
            catch { }
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string letter = drive.RootDirectory.FullName.TrimEnd('\\');
                    if (drive.DriveType != DriveType.Removable && !usbLetters.Contains(letter)) continue;
                    string candidate = Path.Combine(drive.RootDirectory.FullName, "ventoyhdd");
                    if (Directory.Exists(candidate)) { found = candidate; break; }
                }
                catch { }
            }
            if (found != null)
            {
                copySourceFolder.Text = found;
                WriteLog("找到 VentoyHDD 來源：" + found);
                if (showResult) MessageBox.Show("已找到：\r\n" + found, "VSRS", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (showResult) Warn("找不到任何磁碟根目錄下的 ventoyhdd 資料夾。");
        }

        private void RefreshHardware()
        {
            try
            {
                diskBox.Items.Clear(); foreach (var d in HardwareService.GetDisks()) diskBox.Items.Add(d); if (diskBox.Items.Count > 0) diskBox.SelectedIndex = 0;
                volumeBox.Items.Clear(); foreach (var v in HardwareService.GetVolumes()) volumeBox.Items.Add(v); if (volumeBox.Items.Count > 0) volumeBox.SelectedIndex = 0;
                copyTargetVolume.Items.Clear();
                foreach (var v in HardwareService.GetVolumes().Where(v => v.DiskNumber >= 0 && !v.IsUsb && !v.HasWindows)) copyTargetVolume.Items.Add(v);
                if (copyTargetVolume.Items.Count > 0)
                {
                    int preferred = Enumerable.Range(0, copyTargetVolume.Items.Count)
                        .FirstOrDefault(i => string.Equals(((VolumeInfo)copyTargetVolume.Items[i]).Label, "Ventoy", StringComparison.OrdinalIgnoreCase));
                    copyTargetVolume.SelectedIndex = preferred;
                }
                DetectVentoyHddSource(false);
                WriteLog($"偵測完成：{diskBox.Items.Count} 顆磁碟，{volumeBox.Items.Count} 個本機磁區。");
            }
            catch (Exception ex) { Warn("原生磁碟偵測失敗。請確認程式以系統管理員權限執行。\r\n" + ex.Message); }
        }

        private async Task RunBusyAsync(Func<Task<CommandResult>> action)
        {
            SetBusy(true); WriteLog("開始執行……");
            try { var r = await action(); WriteLog(r.Success ? "完成（ExitCode 0）。" : "執行失敗，ExitCode=" + r.ExitCode); if (!r.Success) Warn("作業未成功，請查看下方紀錄。"); }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy) { installButton.Enabled = captureButton.Enabled = createDiffButton.Enabled = mergeButton.Enabled = copyButton.Enabled = !busy; UseWaitCursor = busy; }
        private void WriteLog(string text) { if (InvokeRequired) { BeginInvoke(new Action<string>(WriteLog), text); return; } log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); }
        private static void Warn(string text) => MessageBox.Show(text, "VSRS", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private void DrawTabItem(object sender, DrawItemEventArgs e)
        {
            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle rect = e.Bounds;
            Color back = selected ? ContentBackColor : TabDarkColor;
            Color fore = selected ? TextColor : Color.White;
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, rect);
            if (selected)
            {
                using (var accent = new SolidBrush(AccentColor))
                    e.Graphics.FillRectangle(accent, rect.Left, rect.Bottom - 4, rect.Width, 4);
            }
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, rect, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static TabPage NewPage(string text) => new TabPage(text) { Padding = new Padding(0), BackColor = ContentBackColor, UseVisualStyleBackColor = false };

        private static FlowLayoutPanel AddContentPanel(TabPage page)
        {
            var panel = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(28, 22, 28, 22),
                BackColor = ContentBackColor
            };
            page.Controls.Add(panel);
            panel.ClientSizeChanged += (s, e) => ResizeFlowChildren(panel);
            AddAuthorLabel(page);
            return panel;
        }

        private static void AddAuthorLabel(TabPage page)
        {
            var author = new Label {
                Text = "作者：楊凱文",
                AutoSize = true,
                ForeColor = Color.FromArgb(105, 120, 136),
                BackColor = ContentBackColor,
                Font = new Font("Microsoft JhengHei UI", 9.5F, FontStyle.Regular),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Padding = new Padding(8, 4, 8, 4)
            };
            Action position = () => {
                author.Left = Math.Max(10, page.ClientSize.Width - author.Width - 22);
                author.Top = Math.Max(10, page.ClientSize.Height - author.Height - 16);
            };
            page.Controls.Add(author);
            author.BringToFront();
            page.ClientSizeChanged += (s, e) => position();
            position();
        }

        private static Label AddTitle(FlowLayoutPanel p, string text)
        {
            var c = new Label {
                Text = text,
                AutoSize = true,
                Font = new Font("Microsoft JhengHei UI", 15F, FontStyle.Bold),
                ForeColor = TextColor,
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
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = TextColor,
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
                UseCompatibleTextRendering = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = AccentColor,
                ForeColor = Color.White,
                Cursor = ActionCursor
            };
            c.FlatAppearance.BorderColor = Color.FromArgb(29, 79, 124);
            c.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 119, 181);
            c.FlatAppearance.MouseDownBackColor = Color.FromArgb(25, 73, 113);
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
            var box = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 12, 5), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = TextColor };
            var button = new Button { Text = "瀏覽…", Dock = DockStyle.Fill, Margin = new Padding(0), AutoSize = false, UseCompatibleTextRendering = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(226, 235, 244), ForeColor = TextColor, Cursor = ActionCursor };
            button.FlatAppearance.BorderColor = Color.FromArgb(170, 187, 204);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(207, 222, 236);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(187, 207, 226);
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

        private static TextBox AddFolderPathBox(FlowLayoutPanel p)
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
            var box = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 12, 5), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, ForeColor = TextColor };
            var button = new Button { Text = "瀏覽…", Dock = DockStyle.Fill, Margin = new Padding(0), AutoSize = false, UseCompatibleTextRendering = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(226, 235, 244), ForeColor = TextColor, Cursor = ActionCursor };
            button.FlatAppearance.BorderColor = Color.FromArgb(170, 187, 204);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(207, 222, 236);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(187, 207, 226);
            button.Click += (s, e) => {
                using (var dialog = new FolderBrowserDialog { Description = "選擇隨身碟根目錄下的 ventoyhdd 資料夾", ShowNewFolderButton = false })
                    if (dialog.ShowDialog() == DialogResult.OK) box.Text = dialog.SelectedPath;
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

        private static void StyleDangerButton(Button button)
        {
            button.BackColor = Color.FromArgb(253, 235, 235);
            button.ForeColor = Color.FromArgb(166, 27, 27);
            button.FlatAppearance.BorderColor = Color.FromArgb(220, 100, 100);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(250, 214, 214);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(245, 190, 190);
        }

        private static Cursor CreateYellowHandCursor()
        {
            try
            {
                using (var color = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var mask = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(color))
                using (var maskGraphics = Graphics.FromImage(mask))
                using (var path = new GraphicsPath())
                using (var fill = new SolidBrush(Color.FromArgb(255, 226, 50)))
                using (var outline = new Pen(Color.FromArgb(35, 35, 35), 2F))
                {
                    g.Clear(Color.Transparent);
                    maskGraphics.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    path.AddPolygon(new[] {
                        new Point(8, 2), new Point(12, 2), new Point(12, 13),
                        new Point(14, 11), new Point(17, 12), new Point(18, 13),
                        new Point(20, 12), new Point(23, 14), new Point(24, 18),
                        new Point(24, 24), new Point(21, 29), new Point(11, 29),
                        new Point(8, 25), new Point(4, 20), new Point(5, 17),
                        new Point(8, 19)
                    });
                    g.FillPath(fill, path);
                    g.DrawPath(outline, path);

                    IntPtr colorBitmap = color.GetHbitmap(Color.FromArgb(0));
                    IntPtr maskBitmap = mask.GetHbitmap(Color.White);
                    try
                    {
                        var info = new IconInfo {
                            fIcon = false,
                            xHotspot = 9,
                            yHotspot = 2,
                            hbmMask = maskBitmap,
                            hbmColor = colorBitmap
                        };
                        IntPtr handle = CreateIconIndirect(ref info);
                        if (handle != IntPtr.Zero) return new Cursor(handle);
                    }
                    finally
                    {
                        DeleteObject(colorBitmap);
                        DeleteObject(maskBitmap);
                    }
                }
            }
            catch { }
            return Cursors.Hand;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

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
