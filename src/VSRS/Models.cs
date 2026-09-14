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
        public override string ToString() => $"磁碟 {Number}  |  {SizeText}  |  {(IsUsb ? "USB 外接" : "內接/其他")}  |  {Model}" + (IsBootOrSystem ? "  [含 Windows，高風險]" : "");
    }

    internal sealed class VolumeInfo
    {
        public string DriveLetter { get; set; }
        public string Label { get; set; }
        public string FileSystem { get; set; }
        public ulong Size { get; set; }
        public bool HasWindows { get; set; }
        public int DiskNumber { get; set; } = -1;
        public bool IsUsb { get; set; }
        public string SizeText => (Size / 1024d / 1024d / 1024d).ToString("0.0") + " GB";
        public override string ToString() => $"{DriveLetter}  |  {SizeText}  |  {FileSystem}  |  {Label}  |  {(IsUsb ? "USB 外接" : "內接/其他")}" + (HasWindows ? "  [Windows 來源，可製作 VHDX]" : "");
    }

    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool Success => ExitCode == 0;
    }
}
