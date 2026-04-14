# Registry Search Provider 已建立（已擴充 RecentDocs 解析）

我們已實作了符合您要求的 **LiveRegistrySearchProvider** (程式碼名稱 `RegistrySearchProvider`)。

## 🏛️ 架構設計
這個 Provider 專門設計為「通用只讀查詢」，支持以下查詢條件：
- **Hive**: `HKLM`, `HKCU` 等。
- **Key Path**: 完整鍵值路徑 (如 `Software\Microsoft\Windows\Run`)。
- **Value Name Pattern**: (可選) 數值名稱的 Regex 過濾。
- **Data Pattern**: (可選) 數值內容的 Regex 過濾。
- **Recursive**: (可選) 是否遞迴掃描子機碼。

## 🛡️ 鑑識增強 (Forensic Enhancement)
為了符合規範要求，我們不僅僅是讀取數值：
- **P/Invoke `RegQueryInfoKey`**: 透過 Windows 底層 API 獲取每個 Registry Key 的 **LastWriteTime**。
- **準確時間軸**: 這使得 Registry 事件在 Timeline View 中能以「實際修改時間」呈現，而非連線掃描時間。
- **符合規範**: 產出的 `ActivityEvent` 包含 `EvidenceRef` 追蹤，指向原始的 Hive 與 Key 路徑。

## 🎯 預設查詢 (Default Queries)
目前已預先配置了幾個與鑑識高度相關的查詢，啟動程式時會自動執行並匯入 Timeline：
1. **User Run Key**: `HKCU\...\Run`
2. **System Run Key**: `HKLM\...\Run`
3. **Recent Docs MRU**: `HKCU\...\Explorer\RecentDocs` (遞迴, 用於 RecentFilesView)
4. **Run Dialog MRU**: `HKCU\...\Explorer\RunMRU`

## 📊 整合效益
- 這些 Registry 讀取事件會自動標記為 `Category="Registry"`。
- 在 **Timeline View** 中，您現在將能看到這些 Registry 活動與其他事件的時間軸關聯。
- **RecentDocs 解析已完成**：從 MRU Binary 解析 Shell Item 字串，並輸出 `ParsedPath / FileName / MRU Order` 等欄位。
- 這些欄位會被 **RecentFilesView** 使用，統一呈現 RecentDocs / LNK / JumpList。

## 🖥️ UI 與重新查詢
- **Advanced → Registry Search…** 開啟登錄搜尋對話框：
  - **預設查詢清單**：以表格顯示四筆預設查詢（Name、Key path、Value pattern、Data pattern、Recursive）；可編輯每筆的 Value pattern、Data pattern、Recursive，再按 **Re-run default queries now** 以目前清單執行，結果附加至 Timeline。
  - **Custom query**：可輸入查詢名稱、Hive（HKCU/HKLM/HKU）、Key path，以及可選的 **Value name pattern**（Regex）、**Data pattern**（Regex）、**Recursive**（掃描子機碼），執行後結果附加至 Timeline。
- **僅離線模式（預設）**：當 Settings 勾選「僅離線登錄檔」或未啟用實驗性 Live Registry 時，Registry Search 會顯示 policy disabled 提示，預設查詢與自訂 Live 查詢都會停用。
- **Live 模式（實驗性）**：需同時「取消僅離線」且勾選「允許 Live Registry（實驗性，non-forensic）」；啟用後才可 Re-run 與執行自訂 Live 查詢。仍建議鑑識以 Offline Hive 為主。見 TECH_DEBT §1。

**編譯狀態：** ✅ 已完成並編譯  
**文件更新：** 2026-03-20（連結 TECH_DEBT「中長期排程」Registry 優先 1）；2026-03-20（P/Invoke 變更時與 TECH_DEBT／LIMITATIONS 同步維護說明）；2026-03-05（Live 非鑑識標示、預設查詢可編輯、僅離線時選單/對話框說明）；2026-03-05（registry policy gate：未啟用實驗性 Live Registry 時停用所有 Live 查詢）

## ⚠️ 過渡方案說明 (Technical Debt)
- **中長期優先順序**（是否收斂或移除 Live 路徑、與 major 版本策略）：見 `docs\TECH_DEBT.md` 開頭「**中長期排程**」**優先 1**。
- **RegistrySearchProvider** 使用 Live API（`Microsoft.Win32.RegistryKey`）與 P/Invoke `RegQueryInfoKey`（SafeRegistryHandle）取得 LastWriteTime，已標註為**過渡方案**。
- **維護**：若變更 `RegQueryInfoKey` 宣告、參數型別或 handle 傳遞，請同步更新 `RegistrySearchProvider.cs` 註解、`docs\TECH_DEBT.md` §1／§5 與 `docs\LIMITATIONS.md` §2（見 TECH_DEBT 內「文件與實作同步」）。
- **鑑識優先**：若需離線或鎖定 Hive，請依賴 **OfflineHiveRegistryProvider**（支援 VSS 快照、失敗回退 Live 路徑）。詳見 `OfflineHiveRegistryProvider` 與 `VssSnapshotService`。
