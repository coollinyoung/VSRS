using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace VSRS
{
    /// <summary>
    /// 使用 Windows 原生儲存裝置 API，不依賴 WMI、PowerShell 或 WMIC。
    /// 適用於精簡的 USBOX / Windows PE。
    /// </summary>
    internal static class HardwareService
    {
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const uint IoctlDiskGetLengthInfo = 0x0007405C;
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

        // 列舉實體磁碟，與有磁碟代號的磁區清單不同；未分割硬碟仍可出現在這裡。
        // 資訊查詢失敗只標示 Unknown，不隱藏裝置；容量 0 表示未知，不一定是空磁碟。
        public static List<DiskInfo> GetDisks(Action<string> log = null)
        {
            var windowsDisks = FindWindowsDiskNumbers();
            var result = new List<DiskInfo>();

            foreach (int number in EnumerateDiskNumbers(log))
            {
                string path = @"\\.\PhysicalDrive" + number;
                using (var handle = OpenDevice(path))
                {
                    if (handle == null || handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        log?.Invoke($"磁碟 {number} 已列舉但無法讀取，Windows 錯誤碼：{error}");
                        result.Add(new DiskInfo { Number = number, Model = "資訊無法讀取",
                            BusType = "Unknown", IsBootOrSystem = windowsDisks.Contains(number) });
                        continue;
                    }

                    ulong size = QueryDiskSize(handle);
                    if (size == 0)
                    {
                        using (var readHandle = CreateFile(path, 0x80000000, FileShareRead | FileShareWrite,
                            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                            if (!readHandle.IsInvalid) size = QueryDiskSize(readHandle);
                    }
                    DeviceDescriptor descriptor = QueryDeviceDescriptor(handle);
                    if (size == 0 || string.IsNullOrWhiteSpace(descriptor.Model))
                        log?.Invoke($"磁碟 {number} 回報資訊不完整，仍保留於清單。");
                    result.Add(new DiskInfo {
                        Number = number,
                        Model = string.IsNullOrWhiteSpace(descriptor.Model) ? "未知裝置" : descriptor.Model,
                        BusType = BusTypeName(descriptor.BusType),
                        Size = size,
                        IsUsb = descriptor.BusType == 7 || descriptor.Removable,
                        IsBootOrSystem = windowsDisks.Contains(number)
                    });
                }
            }

            return result.OrderBy(x => x.Number).ToList();
        }

        // QueryDosDevice 回傳雙 NUL 結尾的多字串；緩衝不足時倍增。
        // 列舉不可用才退回 0～255 探測，因此此備援仍有上限。
        private static IEnumerable<int> EnumerateDiskNumbers(Action<string> log)
        {
            // 列舉已註冊的 PhysicalDrive，支援非連續及大於 63 的磁碟編號。
            for (int capacity = 32768; capacity <= 1048576; capacity *= 2)
            {
                char[] buffer = new char[capacity];
                uint length = QueryDosDevice(null, buffer, (uint)capacity);
                if (length != 0)
                {
                    var numbers = new SortedSet<int>();
                    foreach (string name in new string(buffer, 0, (int)length).Split('\0'))
                        if (name.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(name.Substring("PhysicalDrive".Length), out int number) && number >= 0)
                            numbers.Add(number);
                    if (numbers.Count > 0) return numbers;
                    break;
                }
                if (Marshal.GetLastWin32Error() != 122) break;
            }
            log?.Invoke("Windows 裝置名稱列舉未取得磁碟，改用相容性探測。");
            var fallback = new List<int>();
            for (int number = 0; number < 256; number++)
                using (var handle = OpenDevice(@"\\.\PhysicalDrive" + number))
                    if (handle != null && !handle.IsInvalid) fallback.Add(number);
            return fallback;
        }

        [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string name, [Out] char[] target, uint capacity);

        // 只列出已就緒且有代號的本機固定／可移除磁區；不含未掛載分割區。
        // IsUsb 是輔助推測，不是安全保證；單一裝置查詢失敗不應中止其他裝置。
        public static List<VolumeInfo> GetVolumes()
        {
            var disks = GetDisks().ToDictionary(x => x.Number);
            var result = new List<VolumeInfo>();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)) continue;
                    string letter = drive.Name.TrimEnd('\\');
                    int diskNumber = GetDiskNumberForDrive(letter);
                    result.Add(new VolumeInfo {
                        DriveLetter = letter,
                        Label = SafeVolumeLabel(drive),
                        FileSystem = SafeDriveFormat(drive),
                        Size = (ulong)Math.Max(0, drive.TotalSize),
                        HasWindows = Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "Windows", "System32")),
                        DiskNumber = diskNumber,
                        IsUsb = drive.DriveType == DriveType.Removable ||
                                (diskNumber >= 0 && disks.ContainsKey(diskNumber) && disks[diskNumber].IsUsb)
                    });
                }
                catch { }
            }

            return result.OrderByDescending(x => x.HasWindows).ThenBy(x => x.DriveLetter).ToList();
        }

        // 頁籤 2 使用 Volume GUID 列舉，不以 DriveInfo/代號/USB 分類篩選。
        // 掃描只讀 metadata；不因為無代號就掛載所有 EFI/Recovery 磁區。
        // RAW、鎖定或無法讀取的 volume 仍列出；未配置空間/MSR 不是檔案系統 volume。
        public static List<VolumeInfo> GetCaptureVolumes(Action<string> log)
        {
            var disks = GetDisks(log).ToDictionary(x => x.Number);
            var result = new List<VolumeInfo>();
            var name = new StringBuilder(1024);
            IntPtr search = FindFirstVolume(name, (uint)name.Capacity);
            if (search == new IntPtr(-1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "無法列舉磁區");
            try
            {
                do
                {
                    string root = name.ToString();
                    var volume = new VolumeInfo { VolumePath = root, DriveLetter = "", Label = "", FileSystem = "" };
                    // 先加入清單，再讀資訊；任何單一磁區失敗均不可使它消失。
                    result.Add(volume);
                    try
                    {
                        try { volume.DriveLetter = GetVolumeLetters(root).FirstOrDefault() ?? ""; }
                        catch (Exception ex) { log?.Invoke(root + " 掛載路徑查詢失敗：" + ex.Message); }
                        volume.DiskNumber = GetDiskNumberForDrive(root);
                        volume.IsUsb = disks.TryGetValue(volume.DiskNumber, out var disk) && disk.IsUsb;
                        var label = new StringBuilder(261);
                        var fs = new StringBuilder(261);
                        if (GetVolumeInformation(root, label, (uint)label.Capacity,
                            out uint serial, out uint maxName, out uint flags, fs, (uint)fs.Capacity))
                        {
                            volume.Label = label.ToString();
                            volume.FileSystem = fs.ToString();
                        }
                        else volume.ReadError = "資訊無法讀取，Win32=" + Marshal.GetLastWin32Error();
                        if (GetDiskFreeSpaceEx(root, out ulong available, out ulong total, out ulong free))
                            volume.Size = total;
                        // GUID 路徑可直接存取目錄，無需預先分配 C:/D:。
                        uint attributes = GetFileAttributes(root + "Windows");
                        volume.HasWindows = attributes != uint.MaxValue && (attributes & 0x10) != 0;
                    }
                    catch (Exception ex) { volume.ReadError = ex.Message; }
                    if (!string.IsNullOrEmpty(volume.ReadError))
                        log?.Invoke(root + "：" + volume.ReadError);
                    name.Clear();
                } while (FindNextVolume(search, name, (uint)name.Capacity));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) log?.Invoke("磁區列舉未完整結束，Win32=" + error);
            }
            finally { FindVolumeClose(search); }
            return result.OrderByDescending(v => v.HasWindows).ThenBy(v => v.DiskNumber)
                .ThenBy(v => v.VolumePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string[] GetVolumeLetters(string root)
        {
            uint capacity = 512;
            while (capacity <= 1048576)
            {
                var paths = new char[(int)capacity];
                if (GetVolumePathNamesForVolumeName(root, paths, capacity, out uint required))
                    return new string(paths).Split('\0').Where(p => p.Length == 3 &&
                        char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\')
                        .Select(p => p.Substring(0, 2)).ToArray();
                int error = Marshal.GetLastWin32Error();
                if (error != 234) throw new System.ComponentModel.Win32Exception(error);
                capacity = Math.Max(capacity * 2, required);
            }
            throw new IOException("磁區掛載路徑清單過長。");
        }

        // Disk2vhd 的命令列接收磁碟代號。僅在使用者開始製作時為選取的磁區
        // 借用一個空閒代號；現有代號不變動，程序結束（含失敗）即移除借用代號。
        public static CaptureMount MountForCapture(VolumeInfo volume, Action<string> log)
        {
            string root = volume.VolumePath;
            if (string.IsNullOrEmpty(root)) throw new IOException("磁區識別資料已失效，請重新偵測。");
            string existing = GetVolumeLetters(root).FirstOrDefault();
            if (existing != null) return new CaptureMount(existing, root, false, log);
            uint used = GetLogicalDrives();
            if (used == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            int lastError = 0;
            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                if ((used & (1u << (letter - 'A'))) != 0) continue;
                string drive = letter + ":";
                // SetVolumeMountPoint 不覆蓋現有掛載點；另外排除 DOS/SUBST 名稱。
                if (QueryDosDevice(drive, new char[1024], 1024) != 0) continue;
                if (SetVolumeMountPoint(drive + "\\", root))
                {
                    log?.Invoke("暫時分配 " + drive + " 至 " + root);
                    return new CaptureMount(drive, root, true, log);
                }
                lastError = Marshal.GetLastWin32Error();
            }
            throw new IOException("無法為所選磁區分配暫時代號；可能無空閒代號、磁區離線或掛載受限。Win32=" + lastError);
        }

        internal sealed class CaptureMount : IDisposable
        {
            public string DriveLetter { get; }
            private readonly string root;
            private readonly bool owned;
            private readonly Action<string> log;
            private bool disposed;
            internal CaptureMount(string letter, string volumeRoot, bool created, Action<string> logger)
            { DriveLetter = letter; root = volumeRoot; owned = created; log = logger; }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (!owned) return;
                var current = new StringBuilder(1024);
                // 熱插拔後代號可能已改指其他裝置，絕不移除不屬於本次作業的映射。
                if (!GetVolumeNameForVolumeMountPoint(DriveLetter + "\\", current, (uint)current.Capacity) ||
                    !string.Equals(current.ToString(), root, StringComparison.OrdinalIgnoreCase))
                { log?.Invoke("暫時代號映射已改變或無法驗證，未移除：" + DriveLetter); return; }
                if (!DeleteVolumeMountPoint(DriveLetter + "\\"))
                    log?.Invoke("無法移除暫時代號 " + DriveLetter + "，Win32=" + Marshal.GetLastWin32Error());
                else log?.Invoke("已移除暫時代號：" + DriveLetter);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstVolume(StringBuilder name, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextVolume(IntPtr search, StringBuilder name, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindVolumeClose(IntPtr search);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathNamesForVolumeName(string name, [Out] char[] paths, uint length, out uint required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder label, uint labelSize,
            out uint serial, out uint maxName, out uint flags, StringBuilder fs, uint fsSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string root, out ulong available, out ulong total, out ulong free);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetLogicalDrives();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetVolumeMountPoint(string mount, string volume);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteVolumeMountPoint(string mount);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, uint length);

        // IsBootOrSystem 實際表示發現 Windows\System32，不是韌體開機狀態查詢。
        private static HashSet<int> FindWindowsDiskNumbers()
        {
            var result = new HashSet<int>();
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || !Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "Windows", "System32"))) continue;
                    int number = GetDiskNumberForDrive(drive.Name.TrimEnd('\\'));
                    if (number >= 0) result.Add(number);
                }
                catch { }
            }
            return result;
        }

        // 以 VOLUME_DISK_EXTENTS 第一個 extent 取得實體編號，失敗回 -1。
        // 目前只支援第一個 extent 的標示；跨磁碟動態磁區不是完整映射。
        private static int GetDiskNumberForDrive(string driveLetter)
        {
            using (var handle = OpenDevice(driveLetter.StartsWith(@"\\?\",
                StringComparison.Ordinal) ? driveLetter.TrimEnd('\\') : @"\\.\" + driveLetter))
            {
                if (handle == null || handle.IsInvalid) return -1;
                IntPtr output = Marshal.AllocHGlobal(1024);
                try
                {
                    uint returned;
                    if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, output, 1024, out returned, IntPtr.Zero) || returned < 12)
                        return -1;
                    // VOLUME_DISK_EXTENTS：DWORD 數量 + x64 對齊 + 第一個 DISK_EXTENT 的 DiskNumber。
                    return Marshal.ReadInt32(output, 8);
                }
                finally { Marshal.FreeHGlobal(output); }
            }
        }

        // IOCTL 回傳長度至少 8 bytes 才讀取 Int64；取得失敗回 0，由呼叫端備援。
        private static ulong QueryDiskSize(SafeFileHandle handle)
        {
            IntPtr output = Marshal.AllocHGlobal(8);
            try
            {
                uint returned;
                if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, output, 8, out returned, IntPtr.Zero) || returned < 8) return 0;
                long length = Marshal.ReadInt64(output);
                return length > 0 ? (ulong)length : 0;
            }
            finally { Marshal.FreeHGlobal(output); }
        }

        // STORAGE_DEVICE_DESCRIPTOR 的 byte offset 來自 Windows 原生結構。
        // 讀取上限 2048 bytes；無描述資料的外接盒以空描述返回，不淘汰硬碟。
        private static DeviceDescriptor QueryDeviceDescriptor(SafeFileHandle handle)
        {
            IntPtr query = Marshal.AllocHGlobal(12);
            IntPtr output = Marshal.AllocHGlobal(2048);
            try
            {
                for (int i = 0; i < 12; i++) Marshal.WriteByte(query, i, 0);
                for (int i = 0; i < 2048; i++) Marshal.WriteByte(output, i, 0);

                uint returned;
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, output, 2048, out returned, IntPtr.Zero) || returned < 36)
                    return new DeviceDescriptor();

                returned = Math.Min(returned, 2048u);
                bool removable = Marshal.ReadByte(output, 10) != 0;
                uint vendorOffset = (uint)Marshal.ReadInt32(output, 12);
                uint productOffset = (uint)Marshal.ReadInt32(output, 16);
                int busType = Marshal.ReadInt32(output, 28);
                string vendor = ReadAnsiAtOffset(output, vendorOffset, returned);
                string product = ReadAnsiAtOffset(output, productOffset, returned);
                return new DeviceDescriptor {
                    BusType = busType,
                    Removable = removable,
                    Model = (vendor + " " + product).Trim()
                };
            }
            finally
            {
                Marshal.FreeHGlobal(query);
                Marshal.FreeHGlobal(output);
            }
        }

        // 字串 offset 與長度都必須落在驅動實際回傳的緩衝區範圍內。
        private static string ReadAnsiAtOffset(IntPtr buffer, uint offset, uint bufferSize)
        {
            if (offset == 0 || offset >= bufferSize) return string.Empty;
            // 不依賴驅動一定補上 NUL；限制讀取範圍，避免越過原生緩衝區。
            int length = 0;
            while ((uint)length < bufferSize - offset &&
                Marshal.ReadByte(buffer, (int)offset + length) != 0) length++;
            return (Marshal.PtrToStringAnsi(IntPtr.Add(buffer, (int)offset), length) ?? string.Empty).Trim();
        }

        // 以零存取權及共享讀寫查詢 metadata；容量查詢另以唯讀控制代碼重試。
        private static SafeFileHandle OpenDevice(string path)
        {
            return CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }

        private static string SafeVolumeLabel(DriveInfo drive) { try { return drive.VolumeLabel; } catch { return string.Empty; } }
        private static string SafeDriveFormat(DriveInfo drive) { try { return drive.DriveFormat; } catch { return string.Empty; } }

        private static string BusTypeName(int busType)
        {
            switch (busType)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "IEEE 1394";
                case 6: return "Fibre";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "FileBacked";
                case 16: return "Storage Spaces";
                case 17: return "NVMe";
                default: return "Unknown";
            }
        }

        private sealed class DeviceDescriptor
        {
            public int BusType { get; set; }
            public bool Removable { get; set; }
            public string Model { get; set; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }

    internal static class VirtualDiskService
    {
        private const uint VirtualStorageTypeDeviceVhdx = 3;
        private const uint CreateVirtualDiskVersion2 = 2;
        private static readonly Guid MicrosoftVirtualDiskVendor =
            new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B");

        // Version 1 open parameters: read/write child and its immediate parent.
        [StructLayout(LayoutKind.Sequential)]
        private struct OpenParameters { public uint Version; public uint RWDepth; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MergeParameters { public uint Version; public uint MergeDepth; }

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint OpenVirtualDisk(ref VirtualStorageType type, string path,
            uint access, uint flags, ref OpenParameters parameters, out SafeFileHandle handle);
        [DllImport("virtdisk.dll")]
        private static extern uint GetVirtualDiskInformation(SafeFileHandle handle,
            ref uint size, IntPtr information, out uint used);
        [DllImport("virtdisk.dll")]
        private static extern uint MergeVirtualDisk(SafeFileHandle handle, uint flags,
            ref MergeParameters parameters, IntPtr overlapped);

        private static void CheckStatus(uint status)
        {
            if (status != 0) throw new System.ComponentModel.Win32Exception(unchecked((int)status));
        }

        // GET_VIRTUAL_DISK_INFO 的 union 在 x64 offset 8；父檔字串始於 offset 12。
        // 只接受 Windows 已解析成功的絕對父路徑，不自行猜測 base.vhdx。
        private static string ReadParentPath(SafeFileHandle handle)
        {
            // GET_VIRTUAL_DISK_INFO has an 8-byte aligned union.
            uint size = 65548;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(buffer, 3); // GET_VIRTUAL_DISK_INFO_PARENT_LOCATION
                CheckStatus(GetVirtualDiskInformation(handle, ref size, buffer, out uint used));
                if (Marshal.ReadInt32(buffer, 8) == 0)
                    throw new IOException("無法解析所選差分檔的父檔，已停止作業。");
                string parent = Marshal.PtrToStringUni(IntPtr.Add(buffer, 12));
                if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathFullyQualified(parent) || !File.Exists(parent))
                    throw new IOException("找不到有效的父 VHDX：" + parent);
                return Path.GetFullPath(parent);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        // 合併深度固定 1，RWDepth=2 代表需開啟子檔與直接父檔。
        // 原生 API 成功後才關閉子檔、刪除兩個固定目標並重建，失敗不假裝回復。
        // merged 狀態用於區分合併失敗與合併成功但重建失敗。
        public static Task<CommandResult> MergeAndRebuildAsync(string child, Action<string> log)
        {
            return Task.Run(async () => {
                bool merged = false;
                string stage = "開啟子檔與確認父檔";
                try
                {
                    var type = new VirtualStorageType {
                        DeviceId = VirtualStorageTypeDeviceVhdx, VendorId = MicrosoftVirtualDiskVendor
                    };
                    var open = new OpenParameters { Version = 1, RWDepth = 2 };
                    // GET_INFO | METAOPS; never trust a shell exit code before deleting files.
                    uint status = OpenVirtualDisk(ref type, child, 0x00080000 | 0x00200000,
                        0, ref open, out SafeFileHandle handle);
                    CheckStatus(status);
                    string parent;
                    string[] children;
                    using (handle)
                    {
                        parent = ReadParentPath(handle);
                        if (!string.Equals(Path.GetExtension(parent), ".vhdx", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("父檔必須是 VHDX。");
                        string directory = Path.GetDirectoryName(parent);
                        children = new[] { Path.Combine(directory, "temp.vhdx"), Path.Combine(directory, "temp2.vhdx") };
                        foreach (string path in children)
                        {
                            if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("父檔名稱與重建目標相同，已停止以避免刪除父檔：" + parent);
                            if (Directory.Exists(path))
                                throw new IOException("重建路徑已被資料夾占用：" + path);
                            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                throw new IOException("重建目標是連結，已停止：" + path);
                        }
                        log?.Invoke("實際父檔：" + parent);
                        stage = "合併子檔至直接父檔";
                        var merge = new MergeParameters { Version = 1, MergeDepth = 1 };
                        CheckStatus(MergeVirtualDisk(handle, 0, ref merge, IntPtr.Zero));
                        merged = true;
                        log?.Invoke("原生 API 確認合併成功，準備重建兩個差分檔。");
                    } // Close the merged child before deleting it.
                    stage = "刪除父檔目錄中的舊差分檔";
                    foreach (string path in children)
                    {
                        if (!File.Exists(path)) continue;
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        if (File.Exists(path)) throw new IOException("刪除失敗：" + path);
                        log?.Invoke("已刪除：" + path);
                    }
                    stage = "重新建立差分檔";
                    foreach (string path in children)
                    {
                        CommandResult result = await CreateDifferencingVhdxAsync(path, parent, log);
                        if (!result.Success)
                        {
                            result.Output = "父檔已合併成功，但重建失敗：" + path + "\r\n" + result.Output;
                            log?.Invoke(result.Output);
                            return result;
                        }
                        if (!File.Exists(path) || new FileInfo(path).Length == 0)
                            throw new IOException("重建檔案不存在或為空：" + path);
                    }
                    string message = "合併及重建完成：temp.vhdx、temp2.vhdx 均直接以 " + parent + " 為父檔。";
                    log?.Invoke(message);
                    return new CommandResult { ExitCode = 0, Output = message };
                }
                catch (Exception ex)
                {
                    string message = stage + "失敗：" + ex.Message + "\r\n" +
                        (merged ? "父檔已合併成功，重建未完成，可能留下部分差分檔。"
                                : "合併尚未成功；未執行舊差分檔刪除或重建。請先確認相關 VHDX 已卸載且未被占用。");
                    log?.Invoke(message);
                    return new CommandResult {
                        ExitCode = ex is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : -1,
                        Output = message
                    };
                }
            });
        }

        // ParentPath 指定父檔，MaximumSize=0 讓大小繼承父檔；SourcePath 必須為 null。
        // Version 2 的 access mask 必須為 0；控制代碼用完即釋放，避免下次刪除被占用。
        public static Task<CommandResult> CreateDifferencingVhdxAsync(
            string childPath, string parentPath, Action<string> log)
        {
            return Task.Run(() => {
                log?.Invoke("Virtual Disk API 建立差分檔：" + childPath);
                log?.Invoke("父 VHDX：" + parentPath);

                var storageType = new VirtualStorageType {
                    DeviceId = VirtualStorageTypeDeviceVhdx,
                    VendorId = MicrosoftVirtualDiskVendor
                };
                var parameters = new CreateVirtualDiskParameters {
                    Version = CreateVirtualDiskVersion2,
                    UniqueId = Guid.Empty,
                    MaximumSize = 0,
                    BlockSizeInBytes = 0,
                    SectorSizeInBytes = 0,
                    PhysicalSectorSizeInBytes = 0,
                    ParentPath = parentPath,
                    SourcePath = null,
                    OpenFlags = 0,
                    ParentVirtualStorageType = default(VirtualStorageType),
                    SourceVirtualStorageType = default(VirtualStorageType),
                    ResiliencyGuid = Guid.Empty
                };

                try
                {
                    uint status = CreateVirtualDisk(
                        ref storageType, childPath, 0, IntPtr.Zero, 0, 0,
                        ref parameters, IntPtr.Zero, out SafeFileHandle handle);
                    using (handle)
                    {
                        if (status == 0)
                        {
                            log?.Invoke("Virtual Disk API：建立成功。");
                            return new CommandResult { ExitCode = 0, Output = "建立成功：" + childPath };
                        }

                        string message = new System.ComponentModel.Win32Exception(unchecked((int)status)).Message;
                        log?.Invoke($"Virtual Disk API 失敗：錯誤碼 {status}，{message}");
                        return new CommandResult {
                            ExitCode = unchecked((int)status),
                            Output = $"建立差分 VHDX 失敗。Windows 錯誤碼 {status}：{message}"
                        };
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke("Virtual Disk API 無法執行：" + ex.Message);
                    return new CommandResult { ExitCode = -1, Output = ex.ToString() };
                }
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VirtualStorageType
        {
            public uint DeviceId;
            public Guid VendorId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CreateVirtualDiskParameters
        {
            public uint Version;
            // 原生 union 按 8 bytes 對齊；UniqueId 必須從 offset 8 開始，不能緊接 Version。
            private uint unionAlignmentPadding;
            public Guid UniqueId;
            public ulong MaximumSize;
            public uint BlockSizeInBytes;
            public uint SectorSizeInBytes;
            public uint PhysicalSectorSizeInBytes;
            [MarshalAs(UnmanagedType.LPWStr)] public string ParentPath;
            [MarshalAs(UnmanagedType.LPWStr)] public string SourcePath;
            public uint OpenFlags;
            public VirtualStorageType ParentVirtualStorageType;
            public VirtualStorageType SourceVirtualStorageType;
            public Guid ResiliencyGuid;
        }

        [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
        private static extern uint CreateVirtualDisk(
            ref VirtualStorageType virtualStorageType,
            string path,
            uint virtualDiskAccessMask,
            IntPtr securityDescriptor,
            uint flags,
            uint providerSpecificFlags,
            ref CreateVirtualDiskParameters parameters,
            IntPtr overlapped,
            out SafeFileHandle handle);
    }

    internal static class ProcessService
    {
        // 等待外部程式結束；stdout/stderr 分別非同步讀取，避免管線塞滿造成死鎖。
        // 兩個事件共享 StringBuilder，必須加鎖。log 回呼不可直接修改 WinForms 控制項。
        // ExitCode 0 只表示程序回報成功，呼叫端仍須驗證所需檔案或 API 結果。
        public static async Task<CommandResult> RunAsync(string fileName, string arguments, Action<string> log, string workingDirectory = null)
        {
            var output = new StringBuilder();
            var outputLock = new object(); // stdout/stderr 事件可能由不同執行緒同時呼叫。
            var psi = new ProcessStartInfo(fileName, arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppDomain.CurrentDomain.BaseDirectory
            };
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { lock (outputLock) { output.AppendLine(e.Data); } log?.Invoke(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { lock (outputLock) { output.AppendLine(e.Data); } log?.Invoke(e.Data); } };
                try
                {
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    await Task.Run(() => p.WaitForExit());
                    lock (outputLock)
                        return new CommandResult { ExitCode = p.ExitCode, Output = output.ToString() };
                }
                catch (Exception ex) { log?.Invoke(ex.Message); return new CommandResult { ExitCode = -1, Output = ex.ToString() }; }
            }
        }

        // 保留的 DiskPart 輔助方法；目前差分建立／合併已改用 VirtualDiskService。
        // 腳本採 Unicode 並在 finally 刪除；不同 PE 的 DiskPart 編碼支援需實機驗證。
        public static async Task<CommandResult> RunDiskPartAsync(IEnumerable<string> commands, Action<string> log)
        {
            string diskPart = Path.Combine(Environment.SystemDirectory, "diskpart.exe");
            if (!File.Exists(diskPart))
            {
                const string message = "找不到 Windows DiskPart：WinPE 必須包含 diskpart.exe。";
                log?.Invoke(message);
                return new CommandResult { ExitCode = -1, Output = message };
            }

            var commandList = commands.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            string tempDirectory = FindWritableTempDirectory();
            string script = Path.Combine(tempDirectory, "VSRS_" + Guid.NewGuid().ToString("N") + ".txt");
            log?.Invoke("DiskPart 暫存腳本：" + script);
            foreach (string command in commandList) log?.Invoke("DISKPART> " + command);

            try
            {
                // UTF-16 LE（含 BOM）可保留 WinPE 中的中文與非 ASCII 路徑。
                File.WriteAllLines(script, commandList, Encoding.Unicode);
                return await RunAsync(diskPart, "/s \"" + script + "\"", log);
            }
            catch (Exception ex)
            {
                log?.Invoke("建立或執行 DiskPart 指令檔失敗：" + ex.Message);
                return new CommandResult { ExitCode = -1, Output = ex.ToString() };
            }
            finally { try { File.Delete(script); } catch { } }
        }

        // PE 的 TEMP 可能指向不存在或唯讀位置，逐一建立與寫入探測後才使用。
        private static string FindWritableTempDirectory()
        {
            var candidates = new[] {
                Environment.GetEnvironmentVariable("TEMP"),
                Environment.GetEnvironmentVariable("TMP"),
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                @"X:\Windows\Temp",
                @"X:\Temp",
                AppDomain.CurrentDomain.BaseDirectory
            };

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string probe = null;
                try
                {
                    Directory.CreateDirectory(candidate);
                    probe = Path.Combine(candidate, "VSRS_write_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "test", Encoding.ASCII);
                    File.Delete(probe);
                    return candidate;
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(probe)) { try { File.Delete(probe); } catch { } }
                }
            }

            throw new IOException("找不到可寫入的暫存資料夾，無法建立 DiskPart 指令檔。");
        }
    }

    internal static class ToolLocator
    {
        // 外部 EXE 固定搜尋 Tools、程式同層、Tools\Ventoy；不依賴系統 PATH。
        public static string Find(string fileName)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[] { Path.Combine(root, "Tools", fileName), Path.Combine(root, fileName), Path.Combine(root, "Tools", "Ventoy", fileName) };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}

