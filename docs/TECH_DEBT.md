# 技術債與架構備忘 (Technical Debt)

本文件集中記錄已知技術債、過渡方案與後續重構方向。與 `ARCHITECTURE.md` §7、`還有什麼要做的事項.md` 互補。

**處理狀態摘要**：§1 Registry 中期已實作，Live 模式已標示為「非鑑識」；Live 啟用條件集中於 `RegistryLivePolicy`（見 §1）；P/Invoke 文件同步規則已收斂；UI 使用者觸發之 `Process.Start`／ShellExecute 已集中於 `ShellLaunchHelper`（見「中長期排程」優先 2）；§3 **完整持久化**僅文件化決策框架，**未**實作 canonical store／migration；§2 視圖與 Detach 註冊表已抽出為 ViewRegistryService，LiveTcp 分離工具列的 nullable / NRE 風險已於 2026-03-20 收斂；§3 Session 還原已實作；§4 已預留 ViewRegistryService 供未來 Docking 替換實作；§5 P/Invoke 已加註解與文件連結；`KeyGenerator` 舊 API 測試已降級為相容性測試並補上主路徑／Registry 回歸測試。**中長期工作優先順序**見下方專節（依風險、對 stable 邊界之影響、投資報酬排序）。

---

## 中長期排程（優先順序 + 建議處理方式）

排序原則：**風險**、**對 stable 邊界定義之影響**、**投資報酬**。一句話：**先處理 Registry Live 的產品決策，再做 ShellExecute／Process.Start hardening；Index 完整持久化與完整 Docking 可排到 stable sign-off 之後。**

### 優先 1 — Registry Live 路徑收斂或移除

- **理由**：同時牽涉鑑識語義、Live API／P/Invoke 可信度與產品邊界；為目前技術債中與「鑑識／非鑑識」分界最緊密的一項。現況與過渡說明見 **§1**、**§5**、`docs\RegistrySearch說明.md` 過渡方案一節。
- **目前版本正式決策（2026-03-20）**：
  - **保留** Live Registry 路徑。
  - 其定位明確為 **explicit opt-in** 的 **non-forensic inspector**。
  - **不屬於**預設 stable / forensic workflow。
  - 鑑識主路徑仍以 **Offline Hive** 為準。
  - UI、Agent 與 manifest / metadata 對「是否啟用 Live Registry」的判斷與敘述，必須走同一套政策／閘道，不得各自實作。
  - **是否移除** Live Registry 路徑，延後至**下一個 major 版本規劃**再重新評估。
- **若決定保留**：將 Live 路徑**再隔一層抽象**（單一政策／閘道），避免 UI、Agent、manifest 各寫一套規則。✅ **已實作**：`WinDFIR.Core.Settings.RegistryLivePolicy` 為唯一啟用條件；`CollectionMetadataBuilder.IsLiveRegistryEnabled` 轉送該閘道；`MainWindow` 與 `WinDFIR.Agent` 僅透過此閘道決定是否建立 `RegistrySearchProvider`；Registry Search 自訂查詢重用與啟動時相同的 provider 實例（`RunQueriesAsync`），不再另行 `new`。

### 優先 2 — `Process.Start` / ShellExecute hardening

- **理由**：非最大架構債，但**風險面小、收益直接**，實作成本明顯低於 Docking 或完整 Index 持久化。
- **建議處理方式**：抽**單一 helper**，統一處理：**允許的 scheme／路徑類型**、**正規化**、**失敗記錄**、**錯誤提示**。最低限度：**僅允許**本機檔案、本機資料夾、`http`／`https`，其餘**一律拒絕**；**不要**讓各 View 直接 `Process.Start(...)` 各寫一套。
- ✅ **已實作（UI）**：`WinDFIR.UI.Services.ShellLaunchHelper` — Settings 說明連結走 `TryOpenRegistryHelpLink`（僅應用程式目錄內既有檔案或 `http`／`https`）；Process 檢視「開啟目錄」走 `TryRevealPathInExplorer`（僅已存在的本機檔案 `/select` 或資料夾）。失敗時 `Debug.WriteLine` + `MessageBox`。新增呼叫處請改走此 helper，勿在 View 內直接 `Process.Start`。

### 優先 3 — Index 完整持久化

- **理由**：屬**產品能力升級**，非眼前 release 風險；一旦實作會碰 **schema、版本治理、migration**，應在有明確需求時再開。背景與**開工前必答題**見 **§3**「完整持久化：決策框架」。
- **建議處理方式**：**先定目標再寫 code**（細項已展開於 §3）；**不要**在未定案前實作 canonical store 或大量 migration 邏輯。

### 優先 4 — 完整 Docking UI

- **理由**：Detach／Restore 已可滿足多視窗檢視；此項偏 **UX／架構整潔**，非當前鑑識或 stable release 核心風險。背景見 **§4**。
- **建議處理方式**：**僅在**現有多視窗模式**被證明不足**時再開工。若要做：**維持 ViewRegistryService 邊界**，先做**小型 prototype**，**避免**在 MainWindow **全面重寫**。

---

## 1. Registry 提供者（過渡方案）

**現狀**

- **RegistrySearchProvider**：使用 Live Registry API + P/Invoke `RegQueryInfoKey`（SafeHandle）取得 LastWriteTime；程式內已標註為過渡方案。
- **產品定位**：Live Registry 僅作為 **explicit opt-in** 的 **non-forensic inspector**；**不屬於**預設 stable / forensic workflow，且不可與 Offline Hive 並列為同等鑑識路徑。
- **Live 啟用條件**：僅 `RegistryLivePolicy.IsLiveRegistryEnabled`（`UiSettings`：須關閉「僅離線登錄檔」且開啟「實驗性 Live Registry」）。請勿在 UI／Agent 複製該布林式；manifest／preflight 請用 `CollectionMetadataBuilder.IsLiveRegistryEnabled`（內部同上）。
- **文件與實作同步**：變更 `RegQueryInfoKey` 的 DllImport 宣告、參數型別或 `RegistryKey.Handle` 傳遞方式時，須於**同一 PR／同一變更**更新 `RegistrySearchProvider.cs` 此區註解、`docs\LIMITATIONS.md` §2、`docs\RegistrySearch說明.md` 相關句段，以及本節與 §5 的敘述，避免實作與文件漂移。
- **OfflineHiveRegistryProvider**：鑑識優先路徑，支援 VSS 快照、離線 Hive 解析（SYSTEM/SOFTWARE/NTUSER/USRCLASS 等）。

**過渡計畫**

1. **短期**：維持現狀。Live Registry 供即時檢視與部分查詢；鑑識分析以 Offline Hive（含 VSS）為主，文件與 UI 建議優先使用離線路徑。
2. **中期**：✅ 已實作 — Settings 可勾選「僅離線登錄檔」（Ui.RegistryUseOfflineOnly）；重啟後不加入 RegistrySearchProvider，Advanced → Registry Search 顯示為 offline: custom only。
3. **長期**：目前版本已定案為「保留，但僅作為 explicit opt-in 的 non-forensic inspector」；重大改版時再評估是否移除 Live Registry 依賴。**決策步驟與優先序**見本文件開頭「**中長期排程**」**優先 1**。

**UI 標示**：✅ Live 模式時，選單 ToolTip 與 Registry Search 對話框已標示「Live (non-forensic)」，並建議鑑識使用 Offline Hive（Settings → 僅離線登錄檔）。

**參考**：`ARCHITECTURE.md` §7、`docs\RegistrySearch說明.md`、`docs\LIMITATIONS.md` §2（Rootkit/API hooking）。

---

## 2. UI 與 MainWindow 耦合

**已做**

- **MainViewModel**：集中管理 `SelectedDynamicTabIndex`、`SelectedStaticTabIndex`（TwoWay 綁定至 TabControl）；`CurrentContentKey`、`ToolbarViewType` 由選中分頁導出。
- **工具列**：按鈕顯示由 `ToolbarViewType` 綁定 + `ToolbarViewTypeToVisibilityConverter` 驅動，已移除 code-behind 的 `UpdateToolbarVisibility`。
- **Detach 按鈕**：圖示（Detach/Restore）與 ToolTip 由 `MainViewModel.IsDetachRestoreMode`、`DetachButtonToolTip` 與 `UpdateDetachState(bool)` 驅動，MainWindow 僅在狀態變更時呼叫 `UpdateDetachState`。
- **UpdateSharedContent**：依 `CurrentContentKey` 驅動視圖與 Detach 佔位；Detach 狀態更新後呼叫 `UpdateDetachButtonState()` 同步 ViewModel。
- **ViewRegistryService**：✅ 視圖實例與 Detach 視窗/視圖字典已抽出至 `WinDFIR.UI.Services.ViewRegistryService`；MainWindow 建立視圖後註冊於服務，取得視圖與 Detach 狀態皆透過服務，利於未來 Docking 替換實作。

**未做／可選**

- 若未來改為完整 Docking，可實作另一服務取代 ViewRegistryService，或由 ViewModel 持有視圖鍵對應。

**參考**：`ARCHITECTURE.md` §5、§7。

---

## 3. Index 持久化

**現狀**

- **Session 還原**：✅ 已實作。關閉時自動將目前 Index 寫入 `%AppData%\HostWitness\last_session`（`timeline.json` + `meta.json`）；`meta.json` 含 **`sessionSchemaVersion`**（目前為 1），還原時若版本高於目前程式支援會拒絕載入並提示更新；`TryLoadSession` 對缺欄之舊 meta 仍相容。另提供 **File → Save session now** 手動儲存。
- **實作位置**：`WinDFIR.Core.Snapshot.SessionPersistence`（Save / GetSavedSessionInfo / LoadEvents）、MainWindow Closing / Loaded / SaveSessionMenuItem。
- **與「完整持久化」的關係**：`last_session` 屬**工作階段／還原用途**的快照式 JSON，**不是**長期 canonical store；匯出至 SQLite 等路徑若存在，亦應與下述「單一真實來源」決策對齊，避免兩套並行規則。

**可選方向（中長期）**

- 技術選項仍可能包含 SQLite、自訂二進位或其他 store，以支援離線查詢、格式版本與實體鍵序列化；**實際選型應排在決策框架之後**。規劃優先序見「**中長期排程**」**優先 3**。

### 完整持久化：決策框架（開工前必答，先文件後 code）

**觸發條件**：有明確產品需求（例如：必須離線查詢百萬級事件、必須跨機交換「調查專案」、或必須與第三方工具鏈對接）再啟動；否則維持 Session 還原與既有匯出路徑即可。

**1 — 產品定位（主類型須定一個為主，可宣告次要目標）**

| 類型 | 涵義 | 對設計的含意（摘要） |
|------|------|----------------------|
| **Session cache** | 延續上次工作狀態、快速還原 | 可接受與記憶體模型相近的簡化 schema、壽命短、版本可較鬆；**不**必承諾與 snapshot／manifest 長期相容。 |
| **分析資料庫** | 本機為主的查詢與關聯 | 強調索引、查詢計畫、容量與**單一 canonical store**；schema 與 migration 是核心成本。 |
| **可交換格式** | 給他機或他工具匯入／匯出 | 強調**穩定欄位語意**、版本號、向後相容策略與**golden sample**；與「僅內部快取」要求不同。 |

**2 — 單一 canonical store**

- 決定「執行期與儲存後」的**唯一權威**在哪（例如：僅 SQLite、僅某匯出包、或記憶體 + 明確 replay 規則）。  
- 避免 UI、Core、匯出器各自延伸一套欄位或路徑慣例而不經同一層。

**3 — Schema version**

- 訂出版本欄位放哪（檔頭、表、manifest 旁掛 meta 等）。  
- 訂出**相容範圍**（例如：同一 major 內向上相容、跨 major 需遷移工具）。

**4 — Migration policy**

- 載入舊版時：**拒絕並提示**、**自動升級**、或**唯讀舊檔 + 另存新檔** — 須明文選擇與 UI 行為。  
- 界定誰負責遷移（啟動時、匯入精靈、獨立 CLI）。

**5 — 實作與驗證順序（在 1–4 定案後）**

- Importer／Exporter 與 UI 流程。  
- **Golden sample**：固定小／中樣本（含邊界欄位）納入測試，版本 bump 時必跑。  

**參考**：`ARCHITECTURE.md`（Session／Index 描述）、`待修復問題記錄.md`（若有開案連結）。

---

## 4. Docking / 獨立視窗

**現狀**：動態與靜態分頁皆支援「Detach to Window」與「Restore to Main Window」；分離後浮動視窗有獨立工具列。視圖與 Detach 狀態已由 **ViewRegistryService** 持有，MainWindow 僅透過服務存取，利於未來替換為 Docking 實作。

**可選方向**：完整 Docking（可拖曳、可停靠、可儲存版面）需較大 UI 重構；可實作新服務取代 ViewRegistryService 以提供 Docking 行為；目前以 Detach/Restore 滿足「多視窗同時檢視」需求。規劃順序見「**中長期排程**」**優先 4**。

---

## 5. P/Invoke 與安全

**現狀**：RegistrySearchProvider 使用 `RegQueryInfoKey(SafeRegistryHandle)`，未使用 `DangerousGetHandle`；已於程式內加註解標明過渡方案並連結 `docs\TECH_DEBT.md` §5、`docs\LIMITATIONS.md` §2。鑑識優先使用 Offline Hive。**維護規則**與 §1「文件與實作同步」相同：P/Invoke 介面或 handle 用法有變時，程式註解與上述文件一併更新。

**參考**：`docs\還有什麼要做的事項.md`、`docs\RegistrySearch說明.md`。與 Live Registry 相關之**產品決策優先序**見開頭「**中長期排程**」**優先 1**；**ShellExecute** 類 hardening 見**優先 2**。

---

**文件更新**：2026-03-20（寫入 Registry Live 正式決策：保留，但僅作為 explicit opt-in 的 non-forensic inspector；§3 Index 決策框架；`ShellLaunchHelper`；`RegistryLivePolicy`／Registry Search；中長期排程／短期債／§1／§5 P/Invoke）；2026-03-19（專案狀態同步）；2026-03-05（§1 Live 非鑑識標示；§2 ViewRegistryService 抽出；§3 Session 還原已實作；§4 ViewRegistryService 預留 Docking；§5 P/Invoke 註解與文件連結）
