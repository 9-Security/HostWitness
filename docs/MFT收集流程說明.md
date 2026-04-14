# MFT 收集流程說明（HostWitness）

本專案從磁碟載入 `$MFT` 時，使用 **Load from Volumes...** 可一次選多個 NTFS 磁區。每個來源都會建立自己的 MFT tab，避免不同 volume 的 `RecordIndex` 與路徑資訊混在一起。若改用 **Load MFT file...** 載入匯出的 `$MFT` 檔，程式會自動偵測常見 record size（1024 / 4096 bytes）。

## 流程（Load MFT file... → 選擇匯出檔）

| 階段 | 說明 |
|------|------|
| **1. 選擇檔案** | 在 MFT 分頁按 **Load MFT file...**，選取單一 `$MFT` / MFT 匯出檔。 |
| **2. 建立來源 tab** | 每個檔案來源各自對應一個 tab；若同一路徑已載入，則覆寫原 tab。 |
| **3. 自動偵測 record size** | 解析前會先以常見候選值（1024 / 4096 bytes）估算可成功解析的記錄數，選擇較合理的大小。 |
| **4. 解析與顯示** | 成功解析後，狀態列會附上採用的 record size；若偵測結果接近，仍會在狀態列提示這是 auto-detected / ambiguous 的結果。 |

## 流程（Load from Volumes... → 選擇磁碟機）

| 階段 | 說明 |
|------|------|
| **1. 選擇磁碟機** | 在 MFT 分頁按 **Load from Volumes...**，用 Ctrl / Shift 選取一個以上 NTFS 磁碟機（例如 `C:`、`D:`）。 |
| **2. 建立來源 tab** | 每個選取的磁碟機都會開成自己的 tab；若該來源先前已載入，則覆寫原 tab，不會新增重複分頁。 |
| **3. 優先嘗試 raw volume** | 先開啟 `\\.\\X:` 直接讀 NTFS boot sector；若 live volume 無法直接成功，再嘗試 PhysicalDrive partition fallback。 |
| **4. 備援路徑** | 若 raw volume 失敗，改試 Backup privilege 直接讀 `\\.\\X:\\$MFT`；再失敗才建立 VSS snapshot 讀取 `$MFT`。 |
| **5. 解析與顯示** | 成功讀得位元組流後，解析 MFT 記錄、補 full path，並只更新該來源 tab 的表格、分頁、狀態與匯出內容；若命中 100 MB 讀取上限，狀態列會追加結果可能不完整的警告。 |

若所有路徑都失敗，該來源 tab 的狀態列會顯示 raw volume / Backup privilege / VSS 的失敗原因。此時請先確認目標磁碟機是本機、已解鎖的 NTFS 分割區；若仍失敗，再改用「Load MFT file...」載入事先匯出的 `$MFT`。

## UI 行為

- **每個來源一個 tab**：`C:`、`D:`、匯入檔案等都分開顯示，沒有合併的 All view。
- **每個 tab 皆有自己的分頁與篩選**：單頁預設 500 筆，可切換 100／250／500／1000／2000／5000 筆。
- **匯出針對目前 tab**：CSV/JSON 只匯出目前選中 tab 的篩選結果。
- **狀態列會顯示來源補充資訊**：檔案來源會附上 auto-detected record size；磁碟型來源若被 100 MB 讀取上限截斷，會附上結果可能不完整的警告。
- **舊時間戳保留**：1980 年前的 FILETIME 不會直接被清掉，`Time-stomp?` 仍會繼續比較 `$STANDARD_INFORMATION` 與 `$FILE_NAME`。
- **Refresh 已移除**：為避免已載入結果消失或重跑，若要重讀來源請重新使用 `Load MFT file...` 或 `Load from Volumes...`。

## 實作位置

- **磁碟機載入**：`WinDFIR.Core/IO/RawDiskReader.cs`（`ReadMftFromVolume`、`ReadMftFromVolumeViaBackupPrivilege`、`ReadMftFromVolumeViaVss`、`TryResolveVolumeToPhysicalDisk`）。
- **來源 / tab 管理**：`WinDFIR.UI/ViewModels/MftViewModel.cs`（`Tabs`、`SelectedTab`、`LoadFromVolumesAsync`、`GetOrCreateVolumeTab`）。
- **每個 tab 的表格狀態**：`WinDFIR.UI/ViewModels/MftTabViewModel.cs`（分頁、篩選、匯出、載入狀態）。
- **對話框**：`WinDFIR.UI/Views/LoadMftFromRawDialog.xaml`（多選磁碟機 UI）。
- **分頁 UI**：`WinDFIR.UI/Views/MftView.xaml`（外層 TabControl + 各 tab 的 DataGrid / pager）。
- **解析**：`WinDFIR.Core/Mft/MftParser.cs`（以 `$MFT` 位元組流與記錄大小解析 MFT 記錄）。
