請將第三方工具放在此資料夾：

1. disk2vhd64.exe（必要，VSRS 在 x64 WinPE 中實際呼叫）\n   disk2vhd.exe 可保留，但 VSRS 不會呼叫它
2. Ventoy2Disk_X64.exe、Ventoy2Disk.exe，以及 Ventoy Windows 壓縮包內與它們同層的必要檔案

建議目錄：
Tools\disk2vhd64.exe\nTools\disk2vhd.exe（可選）
Tools\Ventoy\Ventoy2Disk_X64.exe（VSRS 在 WinPE 中實際呼叫此檔案）
Tools\Ventoy\Ventoy2Disk.exe
Tools\Ventoy\ventoy\...

基於授權與版本更新考量，VSRS 倉庫不附帶這些第三方二進位檔。
