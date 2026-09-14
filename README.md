# VSRS（Ventoy Standalone Restore System）

VSRS 是為 USBOX 7.0 / Windows PE 設計的 C# WinForms 桌面工具，提供四個頁籤：

1. 安裝 Ventoy 到指定實體硬碟，標示 USB 外接或內接/其他磁碟。
2. 選擇含 Windows 的磁區，透過 Microsoft Disk2vhd 建立 VHDX。
3. 使用 Windows DiskPart 建立差分 VHDX，或把子 VHDX 合併回上一層父磁碟。
4. 搜尋隨身碟根目錄下的 `ventoyhdd` 資料夾，將其中全部內容原樣複製到 Ventoy 內接磁碟根目錄。

介面支援 Windows/WinPE 高 DPI 縮放；內容已改用自動垂直排版，不再依賴固定座標，因此中文標籤、按鈕、下拉欄與路徑欄不會互相重疊；較小解析度下可使用頁面捲軸。

頁籤採深淺對比主題：未選取頁籤為深藍灰色，選取頁籤為淺色並加上藍色底線；內容區、輸入欄、一般按鈕與危險操作按鈕使用一致的色彩層級。

Windows 10/11 支援時，程式標題列也會套用深藍灰背景、白色文字與藍色邊框；精簡 WinPE 若沒有 DWM，會自動略過標題列著色而不影響主要功能。

頁籤與可點擊按鈕使用程式繪製的黃色手型游標及深色外框，方便在淺色和深色區域辨識；不支援自訂游標的 WinPE 會自動退回系統手型。

> **WinPE 相容性：** 磁碟與磁區偵測使用 Windows 原生 `CreateFile`／`DeviceIoControl` API，不依賴 WMI、WMIC 或 PowerShell。

> **重要：** 本專案仍是 Windows Forms，只是由依賴 .NET Framework 4.8 改為 .NET 8 自包含單檔發行。Microsoft 的自包含發行會將執行所需 Runtime 一起部署，因此 PE 不需安裝 .NET。第一次發行會下載 Runtime 與 NuGet 套件，需可連上網路。

> **重要警告**：Ventoy 安裝與 VHDX 合併可能造成永久資料遺失。第一版請先用沒有重要資料的測試電腦及測試硬碟驗證，勿直接用於正式電腦。

## 系統需求

- Visual Studio 2026（或支援 .NET 8 的 Visual Studio）
- 工作負載：`.NET 桌面開發`
- 需要安裝：.NET 8 SDK
- 目標：Windows Forms、.NET 8、win-x64、自包含單一 EXE
- USBOX 7.0 WinPE 不必包含 .NET、WMI 或 StorageWMI；頁籤 3 仍使用 PE 內建的 DiskPart
- 第三方工具：Ventoy Windows 版本、Microsoft Sysinternals Disk2vhd

## Visual Studio 2026：第一次開啟與編譯

1. 在 GitHub 專案頁按綠色 **Code**，選 **Download ZIP**。
2. 將 ZIP 解壓縮，例如 `D:\VSRS`。
3. 啟動 Visual Studio Installer，確認已安裝 **.NET 桌面開發** 工作負載及 **.NET 8 SDK**。
4. 開啟 Visual Studio 2026，選 **開啟專案或方案**。
5. 選取解壓縮資料夾中的 `VSRS.sln`。
6. 在方案總管以滑鼠右鍵點選 **VSRS 專案**（不是方案），選 **發行**。
7. 選取倉庫內建的 `WinPE-x64` 發行設定，再按 **發行**。
8. 成功後，自包含單檔程式位於：`publish\win-x64\VSRS.exe`。

也可以在 PowerShell 執行 `scripts\Build-Release.ps1`。請注意：只按「建置方案」產生的檔案不是正式的自包含發行檔，放入 PE 時應使用 `publish\win-x64\VSRS.exe`.

## 放入必要工具

請保持 Ventoy 官方 Windows 壓縮包的檔案結構，複製到編譯輸出目錄：

```text
publish\win-x64\
├─ VSRS.exe（已內含 .NET 8 Runtime）
└─ Tools\
   ├─ disk2vhd.exe
   └─ Ventoy\
      ├─ Ventoy2Disk_X64.exe（VSRS 實際呼叫）
      ├─ Ventoy2Disk.exe
      └─ ventoy\ ...（Ventoy 官方包的其餘檔案）
```

VSRS 不在 GitHub 內附第三方 EXE；請只從官方來源下載。第一次執行 Disk2vhd 時，程式會加上 `-accepteula` 參數。

在 USBOX WinPE 中，VSRS 固定呼叫同層的 `Ventoy2Disk_X64.exe`，不會呼叫可能在 PE 中出錯的 `Ventoy2Disk.exe`。

## 加入 USBOX 7.0

USBOX 不同版本的「外置程式」資料夾名稱可能不同，建議先用最容易回復的方式測試：

1. 建立 `VSRS` 資料夾，放入 `publish\win-x64\VSRS.exe` 及完整 `Tools` 資料夾。
2. 複製到 USBOX 可寫入的外置程式區或 USB 隨身碟。
3. 進入 USBOX WinPE，先直接雙擊 `VSRS.exe` 測試。
4. 確認可開啟後，再依 USBOX 的桌面捷徑功能，建立指向 `VSRS.exe` 的捷徑。
5. 自包含版本不需要 WinPE-NetFX、WinPE-WMI 或 WinPE-StorageWMI；磁碟偵測已改用 Windows 原生 DeviceIoControl API。

## 三個頁籤的使用方式

### 1. Ventoy 安裝

- 按「重新偵測磁碟」。
- 同時核對磁碟編號、容量、型號及 USB/內接標示。
- 含 Windows 的磁碟會顯示 `[含 Windows，高風險]` 警告，但不再強制鎖定；仍須勾選允許內接磁碟、輸入磁碟編號並通過最後確認。
- 要操作內接/非 USB 磁碟時，還必須勾選允許選項；程式只在此情況固定加入 Ventoy `/GPT /FS:NTFS /NOUSBCheck`，使用 GPT 分割樣式並將 Ventoy 資料磁區格式化為 NTFS。
- 按安裝後，必須再次輸入磁碟編號，再確認資料清除警告。

USB/內接是依 WMI 的 InterfaceType、PNPDeviceID 與 MediaType 綜合判斷。某些 USB-SATA/NVMe 橋接晶片可能回報為 SCSI，因此這只是輔助標示，容量與型號才是最後核對依據。

### 2. Windows 磁區轉 VHDX

- 程式會將具有 `Windows\System32` 的磁區標記為 `[Windows 來源，可製作 VHDX]`。
- WinPE 內離線 Windows 不一定是 C:，請以標記、容量及標籤判斷。包含 Windows 的內接、系統或開機磁區都允許製作 VHDX，不會被鎖定。
- 選擇輸出 VHDX 後開始建立。輸出位置不可放在來源磁區，並應保留足夠空間。

### 3. 差分與合併

- 建立：選父 VHDX 與尚不存在的子 VHDX 路徑。
- 父 VHDX 移動位置後，子磁碟的父路徑關聯可能失效。
- 合併：選子 VHDX，DiskPart 的 `merge vdisk depth=1` 會把變更寫回直接父層。
- 合併前必須確保 VHDX 未掛載、未被虛擬機使用，並先備份父、子檔案。

### 4. 複製 VentoyHDD 資料

- 按「自動搜尋隨身碟」，程式會尋找各磁碟根目錄下名為 `ventoyhdd` 的資料夾，也可按「瀏覽」手動選取。
- 目的磁區只列出已辨識為內接、且沒有偵測到 Windows 的磁區，並優先選取磁碟標籤為 `Ventoy` 的磁區。
- 複製的是 `ventoyhdd` 資料夾內的所有內容，不會在目的磁碟再建立一層 `ventoyhdd`。
- 子資料夾結構會保持不變；目的地的同名檔案會被覆蓋，來源檔案不會刪除。
- 執行前會再次列出完整來源及目的地，必須確認後才會開始。

## 程式結構

```text
src/VSRS/
├─ MainForm.cs       三頁籤介面與操作流程
├─ Services.cs       原生磁碟偵測、執行外部程式與 DiskPart
├─ Models.cs         磁碟、磁區與命令結果模型
├─ Program.cs        程式入口
├─ app.manifest      強制系統管理員權限
└─ VSRS.csproj       .NET 8 自包含 WinForms 專案
```

## 開發狀態與測試順序

這是可編譯的第一版骨架。由於 Ventoy 版本、USBOX 元件與各 USB 橋接晶片回報方式不同，正式使用前依序測試：

1. 一顆空白 USB 隨身碟。
2. 一顆沒有資料的 USB 外接 SSD。
3. VHDX 建立、掛載並檢查 Windows 檔案。
4. 用複製的測試父 VHDX 建立差分、寫入測試檔案、卸載、合併。

發現問題時，請保留下方黑色紀錄框的完整文字、Ventoy 版本、USBOX 版本及磁碟型號，以便修正。
