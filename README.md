# VSRS（Ventoy Standalone Restore System）

VSRS 是為 USBOX 7.0 / Windows PE 設計的 C# WinForms 桌面工具，提供三個頁籤：

1. 安裝 Ventoy 到指定實體硬碟，標示 USB 外接或內接/其他磁碟。
2. 選擇含 Windows 的磁區，透過 Microsoft Disk2vhd 建立 VHDX。
3. 使用 Windows DiskPart 建立差分 VHDX，或把子 VHDX 合併回上一層父磁碟。

> **重要警告**：Ventoy 安裝與 VHDX 合併可能造成永久資料遺失。第一版請先用沒有重要資料的測試電腦及測試硬碟驗證，勿直接用於正式電腦。

## 系統需求

- Visual Studio 2026（或支援 .NET Framework 4.8 的 Visual Studio）
- 工作負載：`.NET 桌面開發`
- 目標：.NET Framework 4.8、x64
- USBOX 7.0 WinPE 必須含：.NET Framework、WMI、StorageWMI、DiskPart
- 第三方工具：Ventoy Windows 版本、Microsoft Sysinternals Disk2vhd

## Visual Studio 2026：第一次開啟與編譯

1. 在 GitHub 專案頁按綠色 **Code**，選 **Download ZIP**。
2. 將 ZIP 解壓縮，例如 `D:\VSRS`。
3. 啟動 Visual Studio Installer，確認已安裝 **.NET 桌面開發** 工作負載及 **.NET Framework 4.8 targeting pack**。
4. 開啟 Visual Studio 2026，選 **開啟專案或方案**。
5. 選取解壓縮資料夾中的 `VSRS.sln`。
6. 上方組態選 `Release`，平台選 `x64`。
7. 選單 **建置 → 建置方案**。成功後輸出位於：
   `src\VSRS\bin\x64\Release\net48\`

如果平台清單沒有 x64：選 **建置 → 組態管理員**，在「使用中的方案平台」新增 `x64`，從 `Any CPU` 複製設定。

## 放入必要工具

請保持 Ventoy 官方 Windows 壓縮包的檔案結構，複製到編譯輸出目錄：

```text
net48\
├─ VSRS.exe
└─ Tools\
   ├─ disk2vhd.exe
   └─ Ventoy\
      ├─ Ventoy2Disk.exe
      └─ ventoy\ ...（Ventoy 官方包的其餘檔案）
```

VSRS 不在 GitHub 內附第三方 EXE；請只從官方來源下載。第一次執行 Disk2vhd 時，程式會加上 `-accepteula` 參數。

## 加入 USBOX 7.0

USBOX 不同版本的「外置程式」資料夾名稱可能不同，建議先用最容易回復的方式測試：

1. 將整個 `net48` 輸出資料夾改名為 `VSRS`。
2. 複製到 USBOX 可寫入的外置程式區或 USB 隨身碟。
3. 進入 USBOX WinPE，先直接雙擊 `VSRS.exe` 測試。
4. 確認可開啟後，再依 USBOX 的桌面捷徑功能，建立指向 `VSRS.exe` 的捷徑。
5. 若顯示缺少 CLR/.NET，需在 USBOX 勾選或加入 WinPE-NetFx；若磁碟清單偵測失敗，需加入 WinPE-WMI 與 WinPE-StorageWMI。

## 三個頁籤的使用方式

### 1. Ventoy 安裝

- 按「重新偵測磁碟」。
- 同時核對磁碟編號、容量、型號及 USB/內接標示。
- 系統磁碟會顯示 `[系統保護]` 並禁止操作。
- 要操作內接/非 USB 磁碟時，還必須勾選允許選項；程式只在此情況加入 Ventoy `/NOUSBCheck`。
- 按安裝後，必須再次輸入磁碟編號，再確認資料清除警告。

USB/內接是依 WMI 的 InterfaceType、PNPDeviceID 與 MediaType 綜合判斷。某些 USB-SATA/NVMe 橋接晶片可能回報為 SCSI，因此這只是輔助標示，容量與型號才是最後核對依據。

### 2. Windows 磁區轉 VHDX

- 程式會將具有 `Windows\System32` 的磁區標記為 `[偵測到 Windows]`。
- WinPE 內離線 Windows 不一定是 C:，請以標記、容量及標籤判斷。
- 選擇輸出 VHDX 後開始建立。輸出位置不可放在來源磁區，並應保留足夠空間。

### 3. 差分與合併

- 建立：選父 VHDX 與尚不存在的子 VHDX 路徑。
- 父 VHDX 移動位置後，子磁碟的父路徑關聯可能失效。
- 合併：選子 VHDX，DiskPart 的 `merge vdisk depth=1` 會把變更寫回直接父層。
- 合併前必須確保 VHDX 未掛載、未被虛擬機使用，並先備份父、子檔案。

## 程式結構

```text
src/VSRS/
├─ MainForm.cs       三頁籤介面與操作流程
├─ Services.cs       WMI 偵測、執行外部程式與 DiskPart
├─ Models.cs         磁碟、磁區與命令結果模型
├─ Program.cs        程式入口
├─ app.manifest      強制系統管理員權限
└─ VSRS.csproj       .NET Framework 4.8 WinForms 專案
```

## 開發狀態與測試順序

這是可編譯的第一版骨架。由於 Ventoy 版本、USBOX 元件與各 USB 橋接晶片回報方式不同，正式使用前依序測試：

1. 一顆空白 USB 隨身碟。
2. 一顆沒有資料的 USB 外接 SSD。
3. VHDX 建立、掛載並檢查 Windows 檔案。
4. 用複製的測試父 VHDX 建立差分、寫入測試檔案、卸載、合併。

發現問題時，請保留下方黑色紀錄框的完整文字、Ventoy 版本、USBOX 版本及磁碟型號，以便修正。
