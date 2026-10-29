# 頁籤 2：無磁碟代號來源偵測（2026-10-29）

## 原因與修正
原先 GetVolumes 只使用 DriveInfo.GetDrives，忽略 PE 沒有分配代號的 Windows 磁區。
頁籤 2 改用 GetCaptureVolumes / FindFirstVolume、FindNextVolume 列舉 Volume GUID。
不以 USB、內接、是否含 Windows 或是否已有代號篩選；資訊讀取失敗的磁區仍留在清單並附錯誤。
直接透過 GUID 根路徑讀取標籤、檔案系統、容量及 Windows 資料夾。Windows 標記只是資料夾偵測，並非完整 OS 驗證。
頁籤 3/4 原本依賴代號的路徑流程繼續使用 GetVolumes，避免空代號破壞複製路徑。

## 製作 VHDX
MountForCapture 重新查詢選取 GUID 的現有代號；沒有代號時從 Z 至 D 尋找未占用代號。
透過 SetVolumeMountPoint 暫時掛載，仍呼叫 disk2vhd64.exe -c <代號> "<輸出資料夾>\\base.vhdx"。
CaptureMount 的 using 範圍包含外部工具等待，成功或失敗都清理本次建立的掛載點。
既有代號不移除；清理前核對 GUID，熱插拔後映射不同或查詢失敗則保留並記錄原因。
不格式化、不清除磁區、不自動解除 BitLocker、不強制上線離線硬碟。

## 範圍
列出 Windows Volume Manager 可辨識的所有 volume，不受內外接限制。
缺少 VMD/RAID 驅動而連 Windows 都看不到的磁碟，必須先載入驅動。
未配置空間與 MSR 不屬於可轉 Windows 映像的檔案系統 volume；實體磁碟仍由頁籤 1 列舉。
鎖定、RAW、離線或無法掛載的磁區不保證可供 Disk2vhd 擷取，錯誤會保留在紀錄。
強制終止程序或斷電無法保證執行清理；此情況暫時代號可能留在本次 PE 工作階段。

## 驗證
已完成三個修改後 C# 檔案的 tree-sitter 語法檢查及來源整合檢查。
目前環境無 .NET SDK/Windows，未編譯，未執行原生 API 或 Disk2vhd；以下為待執行實機驗證：
1. PE 啟動後對比 mountvol 清單，確認無代號 NTFS Windows 磁區顯示且有 Windows 標記。
2. 同時接入 SATA/NVMe/USB，確認分類不影響來源可見性。
3. 選無代號來源擷取；核對日誌中的 GUID/借用代號與 Disk2vhd 參數。
4. 工具正常完成、非零結束、無法啟動時，確認借用代號移除，既有代號不變。
5. 多個無代號磁區只掛載本次選取者，重新偵測不新增掛載點。
6. RAW/鎖定磁區仍顯示資訊讀取失敗；拔除來源後開始作業顯示錯誤。
7. 以已完成映像驗證檔案內容與原生開機能力。

原生 API 參考：
https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstvolumew
https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setvolumemountpointw
