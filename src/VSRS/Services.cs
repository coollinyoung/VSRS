using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace VSRS
{
    internal static class HardwareService
    {
        public static List<DiskInfo> GetDisks()
        {
            var result = new List<DiskInfo>();
            using (var searcher = new ManagementObjectSearcher("SELECT Index,Model,InterfaceType,PNPDeviceID,MediaType,Size FROM Win32_DiskDrive"))
            foreach (ManagementObject d in searcher.Get())
            {
                int number = Convert.ToInt32(d["Index"]);
                string iface = Convert.ToString(d["InterfaceType"]);
                string pnp = Convert.ToString(d["PNPDeviceID"]);
                string media = Convert.ToString(d["MediaType"]);
                bool usb = Contains(iface, "USB") || Contains(pnp, "USB") || Contains(media, "removable");
                result.Add(new DiskInfo {
                    Number = number, Model = Convert.ToString(d["Model"]) ?? "未知裝置",
                    BusType = iface, Size = ToUInt64(d["Size"]), IsUsb = usb,
                    IsBootOrSystem = IsBootOrSystemDisk(number)
                });
            }
            return result.OrderBy(x => x.Number).ToList();
        }

        public static List<VolumeInfo> GetVolumes()
        {
            var result = new List<VolumeInfo>();
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID,VolumeName,FileSystem,Size,DriveType FROM Win32_LogicalDisk WHERE DriveType=3"))
            foreach (ManagementObject v in searcher.Get())
            {
                string drive = Convert.ToString(v["DeviceID"]);
                result.Add(new VolumeInfo {
                    DriveLetter = drive, Label = Convert.ToString(v["VolumeName"]),
                    FileSystem = Convert.ToString(v["FileSystem"]), Size = ToUInt64(v["Size"]),
                    HasWindows = !string.IsNullOrWhiteSpace(drive) && Directory.Exists(Path.Combine(drive + "\\", "Windows", "System32"))
                });
            }
            return result.OrderByDescending(x => x.HasWindows).ThenBy(x => x.DriveLetter).ToList();
        }

        private static bool IsBootOrSystemDisk(int diskNumber)
        {
            try
            {
                using (var ps = new ManagementObjectSearcher($"SELECT BootPartition FROM Win32_DiskPartition WHERE DiskIndex={diskNumber}"))
                foreach (ManagementObject p in ps.Get())
                {
                    if (Convert.ToBoolean(p["BootPartition"] ?? false)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool Contains(string value, string part) => value?.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        private static ulong ToUInt64(object value) { ulong n; return ulong.TryParse(Convert.ToString(value), out n) ? n : 0; }
    }

    internal static class ProcessService
    {
        public static async Task<CommandResult> RunAsync(string fileName, string arguments, Action<string> log)
        {
            var output = new StringBuilder();
            var psi = new ProcessStartInfo(fileName, arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppDomain.CurrentDomain.BaseDirectory
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
            string script = Path.Combine(Path.GetTempPath(), "VSRS_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(script, commands, Encoding.ASCII);
            try { return await RunAsync(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), "/s \"" + script + "\"", log); }
            finally { try { File.Delete(script); } catch { } }
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
