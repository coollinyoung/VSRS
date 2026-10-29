# VSRS 維護與程式審查紀錄

審查日期：2026-10-29。審查基準：05dcfd3a66ef8018ffda929acc344a8703e6aa95。
本次檢查四個頁籤、Services、Models、Program、專案設定、發行設定及部署腳本。
這是靜態審查，不是 Windows／USBOX 實機驗證；執行環境沒有 .NET SDK，未編譯。

## 修改位置索引

| 想修改的功能 | 主要位置 |
|---|---|
| 頁籤內容、文字、色彩、作者、圖示 | MainForm.cs：建構子、Build*Tab、共用 UI helper |
| 硬碟/磁區偵測 | Services.cs：HardwareService |
| Ventoy 安裝參數 | MainForm.cs：InstallVentoyAsync |
| base.vhdx 輸出路徑與 Disk2vhd | MainForm.cs：CaptureAsync |
| USB 固定基底搜尋、刪除舊差分 | MainForm.cs：FindUsbBaseVhdx / CreateDiffAsync |
| 父檔解析、原生差分建立/合併重建 | Services.cs：VirtualDiskService |
| 完整部署及後續批次檔 | MainForm.cs：CopyVentoyDataAsync |
| 自動/手動還原 | MainForm.cs：CopyRestoreModeAsync / CopyRestoreTree |
| 操作互斥、例外處理、執行緒紀錄 | MainForm.cs：RunBusyAsync / SetBusy / WriteLog |
| 外部工具啟動、stdout/stderr | Services.cs：ProcessService |
| 資料欄位與錯誤結果 | Models.cs |
| 發行與部署 | VSRS.csproj、Properties/PublishProfiles、scripts |

## 固定契約

- Tab1：實體磁碟編號不可當作磁區代號；USB 分類不作為安裝阻擋條件。
- Tab2：資料夾欄位只存資料夾，輸出固定 base.vhdx；建檔後保留小寫正規化。
- Tab3：USB 的 ventoyHDD\os\base.vhdx；temp.vhdx、temp2.vhdx 均直接依賴同一父檔。
- 合併取得真實直接父檔，成功後只清除父檔目錄的兩個固定檔名，再重建；不是可回復交易。
- Tab4：完整部署複製 ventoyhdd 內容至目的根目錄，再執行根目錄隱藏資料夾.bat。
- 右側還原按鈕來源為 Script\auto 或 Script\manual；只覆蓋 os/ventoy，不刪額外檔案，不跳覆蓋確認，不執行批次檔。
- 自動/手動還原全部成功才提示「請拔除隨身碟，重新開機」。
- 目標磁區不恢復 USB/Windows 分類限制。

## 本次修正

1. stdout/stderr 同時寫入 StringBuilder 存在競爭：共用鎖保護寫入與讀出。
2. base.vhdx 兩段改名第二步失敗：嘗試回復原檔名；回復失敗則回報暫存檔的確切位置。
3. 頁籤 2 共用資料夾視窗顯示錯誤提示：參數化說明文字與是否允許新建資料夾。
4. 操作期間仍可刷新/改選目標及關閉程式：集中 busy 狀態、停用頁籤、禁止正常關閉，統一捕捉未處理作業例外。
5. 驅動回傳字串沒有 NUL 時可能越界：限制讀取在實際緩衝區範圍；容量查詢至少回傳 8 bytes 才讀取。
6. 原生 CREATE_VIRTUAL_DISK_PARAMETERS 展平結構未補 union 對齊：新增 4 bytes padding，UniqueId 位於 x64 offset 8。
7. 完整部署與模式覆蓋的複製實作不一致：共用遞迴 helper，支援唯讀目標覆蓋及拒絕遇到的連結。
8. 背景紀錄在關閉視窗後仍可能回呼：加入 disposed/handle 檢查。

## 靜態檢查及限制

- 四個 C# 檔案以 tree-sitter C# 語法解析通過，VSRS.csproj 以 XML parser 解析通過；語法解析不包含型別檢查或編譯。

- 以 Python ctypes 建立 x64 原生佈局模型驗證：UniqueId=8、MaximumSize=24、ParentPath=48、結構大小128。這不等於 .NET Marshal 或原生呼叫測試。
- 確認兩個建立目標均使用同一 parent；合併成功之前不進入刪除流程。
- 確認新資料夾參數只影響頁籤 2，頁籤 4 預設行為保留。
- 未進行編譯、UI 顯示、磁碟寫入、外部工具、PowerShell 或 PE 執行測試。

## 尚待實機驗證/後續改善

- USB 分類仍來自 BusType/Removable；特殊 SCSI/RAID 外接盒即使 Tab1 列出，也可能不被 Tab3/4 自動來源搜尋視為 USB。
- 磁碟列舉及刷新為同步；慢速或異常裝置可能造成 UI 等待。
- 磁區到磁碟映射只使用第一個 extent；跨碟動態磁區不具完整支援。
- base.vhdx 的存在檢查不能證明內容正確/是本次新產生；需進一步以 VHD API 開啟及來源識別驗證。
- 差分刪除/建立與大量檔案覆蓋無交易回復；中斷後可能只剩部分結果。
- 遞迴 helper 拒絕遇到的連結，但不是完整檔案 ID/硬連結/祖先連結驗證。
- 隱藏批次檔不可需要 pause 或輸入；程式目前沒有互動終端、取消與逾時。
- 低解析度/DPI：ResizeFlowChildren 最小寬560，右側欄及作者文字仍需視覺驗證。
- 原生 CreateVirtualDisk sector 參數、父子格式及掛載狀態應用實際512/4096 sector基底驗證。
- RunDiskPartAsync 為保留 helper，目前頁籤3不走這條路；編碼與暫存路徑需另測。

## Windows 測試清單（尚未執行）

1. 使用 WinPE-x64 發行，確認零編譯錯誤，帶上官方 Tools。
2. Tab1：測試內接 SATA/NVMe、USB-SATA、USB-NVMe，資訊失敗仍有清楚標記。
3. Tab2：一般/中文/空白路徑，測試資料夾對話框、base.vhdx 小寫、改名失敗訊息。
4. Tab3：用備份基底測試無舊檔/一個舊檔/兩個舊檔、檔案被占用、多USB來源。
5. 合併：寫入測試內容到子檔、卸載、合併；驗證父檔內容與兩個新差分皆可開啟。
6. Tab4：一般與唯讀同名檔、空子目錄、來源缺少資料夾、批次檔缺少及非零退出。
7. 執行期間不能改選目標或關閉；成功與失敗後控制項皆恢復。
8. 多行 stdout/stderr 並行輸出無例外；視窗關閉後沒有回呼崩潰。

原生結構參考：
https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/ns-virtdisk-create_virtual_disk_parameters
