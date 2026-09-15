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

        public static List<DiskInfo> GetDisks()
        {
            var windowsDisks = FindWindowsDiskNumbers();
            var result = new List<DiskInfo>();

            for (int number = 0; number < 64; number++)
            {
                string path = @"\\.\PhysicalDrive" + number;
                using (var handle = OpenDevice(path))
                {
                    if (handle == null || handle.IsInvalid) continue;

                    ulong size = QueryDiskSize(handle);
                    DeviceDescriptor descriptor = QueryDeviceDescriptor(handle);
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

        private static int GetDiskNumberForDrive(string driveLetter)
        {
            using (var handle = OpenDevice(@"\\.\" + driveLetter))
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

        private static ulong QueryDiskSize(SafeFileHandle handle)
        {
            IntPtr output = Marshal.AllocHGlobal(8);
            try
            {
                uint returned;
                if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, output, 8, out returned, IntPtr.Zero)) return 0;
                long length = Marshal.ReadInt64(output);
                return length > 0 ? (ulong)length : 0;
            }
            finally { Marshal.FreeHGlobal(output); }
        }

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

        private static string ReadAnsiAtOffset(IntPtr buffer, uint offset, uint bufferSize)
        {
            if (offset == 0 || offset >= bufferSize) return string.Empty;
            return (Marshal.PtrToStringAnsi(IntPtr.Add(buffer, (int)offset)) ?? string.Empty).Trim();
        }

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
        public static async Task<CommandResult> RunAsync(string fileName, string arguments, Action<string> log, string workingDirectory = null)
        {
            var output = new StringBuilder();
            var psi = new ProcessStartInfo(fileName, arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppDomain.CurrentDomain.BaseDirectory
            };
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { output.AppendLine(e.Data); log?.Invoke(e.Data); } };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) { output.AppendLine(e.Data); log?.Invoke(e.Data); } };
                try
                {
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    await Task.Run(() => p.WaitForExit());
                    return new CommandResult { ExitCode = p.ExitCode, Output = output.ToString() };
                }
                catch (Exception ex) { log?.Invoke(ex.Message); return new CommandResult { ExitCode = -1, Output = ex.ToString() }; }
            }
        }

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
        public static string Find(string fileName)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[] { Path.Combine(root, "Tools", fileName), Path.Combine(root, fileName), Path.Combine(root, "Tools", "Ventoy", fileName) };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
