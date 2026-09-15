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
        private TextBox vhdOutput, mergeVhd, copySourceFolder;
        private ComboBox copyTargetVolume;
        private Button installButton, captureButton, createDiffButton, mergeButton, copyButton, autoRestoreButton, manualRestoreButton;

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
            AddText(content, @"自動偵測 USB 磁碟中的 ventoyHDD\os\base.vhdx，並在同一資料夾建立 temp.vhdx 與 temp2.vhdx。");
            AddText(content, "兩個差分檔都直接使用 base.vhdx 為父檔。既有 temp.vhdx、temp2.vhdx 會先刪除，再重新建立。", Color.DarkRed);
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
            // 保留左側捲動內容，右側獨立放置兩個還原按鈕，避免互相遮擋。
            var columns = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = ContentBackColor
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            page.Controls.Remove(content);
            columns.Controls.Add(content, 0, 0);
            var actions = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(12, 22, 12, 48), BackColor = ContentBackColor
            };
            columns.Controls.Add(actions, 1, 0);
            page.Controls.Add(columns);
            columns.SendToBack();
            AddTitle(actions, "還原模式");
            AddText(actions, "直接覆蓋左側所選目的磁區的 os 與 ventoy 資料夾。");
            autoRestoreButton = AddButton(actions, "自動還原", 180);
            manualRestoreButton = AddButton(actions, "手動還原", 180);
            autoRestoreButton.Click += async (s, e) => await CopyRestoreModeAsync("auto");
            manualRestoreButton.Click += async (s, e) => await CopyRestoreModeAsync("manual");
            actions.ClientSizeChanged += (s, e) => {
                ResizeFlowChildren(actions);
                int width = Math.Max(100, actions.ClientSize.Width - actions.Padding.Horizontal - 24);
                autoRestoreButton.Width = manualRestoreButton.Width = width;
            };
            AddTitle(content, "複製隨身碟中的 VentoyHDD 資料");
            AddText(content, "來源為 USB 隨身碟根目錄下的 ventoyhdd 資料夾。程式會複製其中所有檔案及子資料夾，並保持原本目錄結構。", Color.FromArgb(36, 83, 125));
            AddText(content, "來源資料夾：");
            copySourceFolder = AddFolderPathBox(content);
            var detect = AddButton(content, "自動搜尋隨身碟", 200);
            detect.Click += (s, e) => DetectVentoyHddSource(true);
            AddText(content, "目的磁區（複製至所選磁區根目錄）：");
            copyTargetVolume = AddCombo(content);
            AddText(content, "列出所有已偵測到的磁區，請核對 Ventoy 目的磁區的代號、容量及標籤。同名檔案將會覆蓋，來源資料不會刪除。", Color.DarkRed);
            AddText(content, "全部複製成功後，會自動執行目的磁區根目錄中的「隱藏資料夾.bat」。");
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

            // 無論使用者或檔案選擇視窗輸入 .VHDX／.Vhdx，都統一使用小寫 .vhdx。
            output = Path.ChangeExtension(output, ".vhdx");

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
            await RunBusyAsync(async () => {
                CommandResult result = await ProcessService.RunAsync(disk2vhd, arguments, WriteLog);
                if (!result.Success) return result;

                string actualFile = null;
                try
                {
                    actualFile = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(path =>
                            string.Equals(Path.GetFileNameWithoutExtension(path),
                                Path.GetFileNameWithoutExtension(output), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetExtension(path), ".vhdx", StringComparison.OrdinalIgnoreCase));

                    if (actualFile == null)
                    {
                        string detail = "Disk2vhd 回報成功，但輸出資料夾中找不到建立的 VHDX 檔案：\r\n" + output;
                        WriteLog(detail);
                        return new CommandResult { ExitCode = -2, Output = detail };
                    }

                    if (!string.Equals(actualFile, output, StringComparison.Ordinal))
                    {
                        string temporaryName = Path.Combine(outputDirectory,
                            "VSRS_case_" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.Move(actualFile, temporaryName);
                        File.Move(temporaryName, output);
                        WriteLog("已將實際副檔名強制改為小寫 .vhdx：" + output);
                    }
                    else
                    {
                        WriteLog("輸出副檔名確認為小寫 .vhdx：" + output);
                    }

                    vhdOutput.Text = output;
                    return result;
                }
                catch (Exception ex)
                {
                    string detail = "VHDX 已建立，但無法將副檔名正規化為小寫 .vhdx。\r\n" +
                                    "實際檔案：" + (actualFile ?? "找不到") + "\r\n" + ex.Message;
                    WriteLog(detail);
                    return new CommandResult { ExitCode = -3, Output = detail };
                }
            });
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

        private static string FindUsbBaseVhdx()
        {
            var usbLetters = new System.Collections.Generic.HashSet<string>(
                HardwareService.GetVolumes().Where(v => v.IsUsb).Select(v => v.DriveLetter),
                StringComparer.OrdinalIgnoreCase);
            var candidates = new System.Collections.Generic.List<string>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    if (drive.DriveType != DriveType.Removable &&
                        !usbLetters.Contains(root.TrimEnd('\\'))) continue;
                    string candidate = Path.Combine(root, "ventoyHDD", "os", "base.vhdx");
                    if (File.Exists(candidate)) candidates.Add(candidate);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (candidates.Count == 0)
                throw new IOException(@"找不到 USB 磁碟中的 ventoyHDD\os\base.vhdx。請確認 USB 已連接且檔案存在。");
            if (candidates.Count != 1)
                throw new IOException("找到多個 USB 基底檔，未刪除任何差分檔。請只保留要操作的 USB 磁碟後重試：\r\n" +
                                      string.Join("\r\n", candidates));
            return candidates[0];
        }

        private async Task CreateDiffAsync()
        {
            await RunBusyAsync(async () => {
                try
                {
                    string parent = await Task.Run(() => FindUsbBaseVhdx());
                    string directory = Path.GetDirectoryName(parent);
                    string[] children = {
                        Path.Combine(directory, "temp.vhdx"),
                        Path.Combine(directory, "temp2.vhdx")
                    };
                    WriteLog("自動偵測基底：" + parent);
                    // 確認父檔可讀，以及兩個目標不是資料夾或連結，再開始刪除。
                    using (var input = File.Open(parent, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length == 0) throw new IOException("base.vhdx 是空檔案，已停止作業。");
                    }
                    foreach (string child in children)
                    {
                        if (Directory.Exists(child))
                            throw new IOException("差分檔路徑已被資料夾占用：" + child);
                        if (File.Exists(child) &&
                            (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("差分檔是連結，已停止作業：" + child);
                    }
                    // 必須先完成兩個舊差分檔的刪除，才建立任何新差分檔。
                    foreach (string child in children)
                    {
                        if (!File.Exists(child)) continue;
                        File.SetAttributes(child, FileAttributes.Normal);
                        File.Delete(child);
                        if (File.Exists(child)) throw new IOException("無法刪除舊差分檔：" + child);
                        WriteLog("已刪除舊差分檔：" + child);
                    }
                    foreach (string child in children)
                    {
                        CommandResult result = await VirtualDiskService.CreateDifferencingVhdxAsync(
                            child, parent, WriteLog);
                        if (!result.Success)
                        {
                            result.Output = "建立失敗：" + child + "\r\n" + result.Output;
                            WriteLog(result.Output);
                            return result;
                        }
                        if (!File.Exists(child) || new FileInfo(child).Length == 0)
                        {
                            string message = "建立結果驗證失敗，檔案不存在或為空：" + child;
                            WriteLog(message);
                            return new CommandResult { ExitCode = -2, Output = message };
                        }
                        WriteLog("檔案確認成功：" + child);
                    }
                    string completed = "已在 " + directory + " 建立 temp.vhdx 與 temp2.vhdx，兩者父檔均為 base.vhdx。";
                    WriteLog(completed);
                    return new CommandResult { ExitCode = 0, Output = completed };
                }
                catch (Exception ex)
                {
                    string message = "差分重建作業停止：" + ex.Message +
                        "\r\n若已開始刪除或建立，資料夾可能只剩部分檔案；請查看紀錄。";
                    WriteLog(message);
                    return new CommandResult { ExitCode = -1, Output = message };
                }
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

        private async Task CopyRestoreModeAsync(string mode)
        {
            var target = copyTargetVolume.SelectedItem as VolumeInfo;
            if (target == null) { Warn("請先選擇左側的目的磁區。"); return; }
            string destination = Path.GetPathRoot(target.DriveLetter + "\\");
            string preferredRoot = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(copySourceFolder.Text))
                    preferredRoot = Path.GetPathRoot(Path.GetFullPath(copySourceFolder.Text.Trim()));
            }
            catch { }

            await RunBusyAsync(async () => {
                return await Task.Run(() => {
                    try
                    {
                        var usbLetters = new System.Collections.Generic.HashSet<string>(
                            HardwareService.GetVolumes().Where(v => v.IsUsb).Select(v => v.DriveLetter),
                            StringComparer.OrdinalIgnoreCase);
                        var candidates = new System.Collections.Generic.List<string>();
                        foreach (DriveInfo drive in DriveInfo.GetDrives())
                        {
                            try
                            {
                                if (!drive.IsReady) continue;
                                string root = drive.RootDirectory.FullName;
                                if (drive.DriveType != DriveType.Removable &&
                                    !usbLetters.Contains(root.TrimEnd('\\'))) continue;
                                if (string.Equals(root, destination, StringComparison.OrdinalIgnoreCase)) continue;
                                string source = Path.Combine(root, "Script", mode);
                                if (Directory.Exists(Path.Combine(source, "os")) &&
                                    Directory.Exists(Path.Combine(source, "ventoy")))
                                    candidates.Add(source);
                            }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                        string selected = candidates.FirstOrDefault(p =>
                            string.Equals(Path.GetPathRoot(p), preferredRoot, StringComparison.OrdinalIgnoreCase));
                        if (selected == null && candidates.Count == 1) selected = candidates[0];
                        if (selected == null)
                        {
                            string message = candidates.Count == 0
                                ? $"找不到 USB 隨身碟中的 Script\\{mode}\\os 與 Script\\{mode}\\ventoy，未複製任何檔案。"
                                : "找到多個 USB 還原來源，請先在左側來源資料夾選擇要使用的 USB 隨身碟中的 ventoyhdd 資料夾。";
                            WriteLog(message);
                            return new CommandResult { ExitCode = -1, Output = message };
                        }
                        WriteLog($"還原模式：{mode}；來源：{selected}；目的：{destination}");
                        int count = 0;
                        foreach (string folder in new[] { "os", "ventoy" })
                            CopyRestoreTree(Path.Combine(selected, folder), Path.Combine(destination, folder), ref count);
                        string completed = $"還原模式 {mode} 複製完成，共覆蓋或新增 {count} 個檔案。";
                        WriteLog(completed);
                        return new CommandResult { ExitCode = 0, Output = completed };
                    }
                    catch (Exception ex)
                    {
                        string message = "還原資料複製失敗（可能已有部分檔案完成覆蓋）：\r\n" + ex.Message;
                        WriteLog(message);
                        return new CommandResult { ExitCode = -1, Output = message };
                    }
                });
            });
        }

        private void CopyRestoreTree(string source, string destination, ref int count)
        {
            // 不追蹤連結，避免寫入所選資料夾以外的位置。
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("來源資料夾是連結，無法複製：" + source);
            if (Directory.Exists(destination) &&
                (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("目的資料夾是連結，無法複製：" + destination);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("來源檔案是連結，無法複製：" + file);
                string output = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(output))
                {
                    FileAttributes attributes = File.GetAttributes(output);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("目的檔案是連結，無法覆蓋：" + output);
                    File.SetAttributes(output, FileAttributes.Normal);
                }
                File.Copy(file, output, true);
                count++;
                WriteLog("已覆蓋或新增：" + output);
            }
            foreach (string directory in Directory.GetDirectories(source))
                CopyRestoreTree(directory, Path.Combine(destination, Path.GetFileName(directory)), ref count);
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
            if (target == null) { Warn("請選擇要複製到的 Ventoy 目的磁區。"); return; }

            string destination = Path.GetPathRoot(target.DriveLetter + "\\");
            string sourceRoot = Path.GetFullPath(source).TrimEnd('\\') + "\\";
            if (sourceRoot.StartsWith(destination, StringComparison.OrdinalIgnoreCase))
            {
                Warn("來源與目的地不可位於同一個目的磁區。"); return;
            }
            if (MessageBox.Show($"來源：{sourceRoot}\r\n目的：{destination}\r\n\r\n將保留目錄結構，並覆蓋目的地的同名檔案。複製完成後會執行目的磁區根目錄中的「隱藏資料夾.bat」。確定開始？",
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
                    WriteLog($"全部檔案複製完成，共 {files} 個檔案。");
                    string batchDirectory = destination;
                    string batchPath = Path.Combine(batchDirectory, "隱藏資料夾.bat");
                    if (!File.Exists(batchPath))
                    {
                        string message = "檔案已複製完成，但找不到後續批次檔：" + batchPath;
                        WriteLog(message);
                        return new CommandResult { ExitCode = -1, Output = message };
                    }

                    WriteLog("開始執行批次檔：" + batchPath);
                    // 批次檔由 cmd.exe 執行；以目的磁區根目錄為工作目錄，支援批次檔中的相對路徑。
                    CommandResult batchResult = await ProcessService.RunAsync(
                        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        "/d /s /c \"\"" + batchPath + "\"\"", WriteLog, batchDirectory);
                    if (!batchResult.Success)
                    {
                        string message = $"檔案已複製完成，但隱藏資料夾.bat 執行失敗，ExitCode={batchResult.ExitCode}。\r\n" +
                                         batchResult.Output;
                        WriteLog(message);
                        return new CommandResult { ExitCode = batchResult.ExitCode, Output = message };
                    }
                    WriteLog("隱藏資料夾.bat 執行完成。");
                    return new CommandResult { ExitCode = 0, Output = $"完成，共複製 {files} 個檔案，並已執行隱藏資料夾.bat。" };
                }
                catch (Exception ex)
                {
                    WriteLog("複製或後續批次作業失敗：" + ex.Message);
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
                foreach (var v in HardwareService.GetVolumes()) copyTargetVolume.Items.Add(v);
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
            try
            {
                var r = await action();
                WriteLog(r.Success ? "完成（ExitCode 0）。" : "執行失敗，ExitCode=" + r.ExitCode);
                if (!r.Success)
                {
                    string detail = string.IsNullOrWhiteSpace(r.Output) ? "沒有其他錯誤資訊。" : r.Output;
                    Warn("作業未成功：\r\n\r\n" + detail + "\r\n\r\n完整過程請查看下方紀錄。");
                }
            }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool busy) { installButton.Enabled = captureButton.Enabled = createDiffButton.Enabled = mergeButton.Enabled = copyButton.Enabled = autoRestoreButton.Enabled = manualRestoreButton.Enabled = !busy; UseWaitCursor = busy; }
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
