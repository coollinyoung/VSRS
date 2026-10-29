using System;

namespace VSRS
{
    // 實體磁碟：Number 是 PhysicalDrive 編號，非 C:/D: 代號。
    // Size 單位為 bytes；0 表示查詢不到；IsUsb 僅是裝置回報的推測。
    internal sealed class DiskInfo
    {
        public int Number { get; set; }
        public string Model { get; set; }
        public string BusType { get; set; }
        public ulong Size { get; set; }
        public bool IsUsb { get; set; }
        // 歷史名稱：實際上只代表磁區內發現 Windows\\System32，不代表目前開機來源。
        public bool IsBootOrSystem { get; set; }
        public string DevicePath => @"\\.\PhysicalDrive" + Number;
        public string SizeText => (Size / 1024d / 1024d / 1024d).ToString("0.0") + " GB";
        public override string ToString() => $"磁碟 {Number}  |  {SizeText}  |  {(IsUsb ? "USB 外接" : "內接/其他")}  |  {Model}" + (IsBootOrSystem ? "  [含 Windows，高風險]" : "");
    }

    // 磁區：DriveLetter 格式為 C:（不含尾端反斜線），DiskNumber=-1 表示映射失敗。
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

    // 外部程式與原生 API 共用的結果。0 成功；-1 一般例外、-2 結果驗證失敗、-3 改名失敗。
    // 正值或其他負值可能是原生工具/Win32 錯誤，不可只看 Success 推斷資料已完整寫入。
    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool Success => ExitCode == 0;
    }
}
