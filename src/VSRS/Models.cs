using System;

namespace VSRS
{
    internal sealed class DiskInfo
    {
        public int Number { get; set; }
        public string Model { get; set; }
        public string BusType { get; set; }
        public ulong Size { get; set; }
        public bool IsUsb { get; set; }
        public bool IsBootOrSystem { get; set; }
        public string DevicePath => @"\\.\PhysicalDrive" + Number;
        public string SizeText => (Size / 1024d / 1024d / 1024d).ToString("0.0") + " GB";
        public override string ToString() => $"磁碟 {Number}  |  {SizeText}  |  {(IsUsb ? "USB 外接" : "內接/其他")}  |  {Model}" + (IsBootOrSystem ? "  [系統保護]" : "");
    }

    internal sealed class VolumeInfo
    {
        public string DriveLetter { get; set; }
        public string Label { get; set; }
        public string FileSystem { get; set; }
        public ulong Size { get; set; }
        public bool HasWindows { get; set; }
        public string SizeText => (Size / 1024d / 1024d / 1024d).ToString("0.0") + " GB";
        public override string ToString() => $"{DriveLetter}  |  {SizeText}  |  {FileSystem}  |  {Label}" + (HasWindows ? "  [偵測到 Windows]" : "");
    }

    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool Success => ExitCode == 0;
    }
}
