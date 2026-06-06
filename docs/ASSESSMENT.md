# HostWitness — 客觀實務評估 (Practical Assessment)

本文件從 **Windows 主機事件回應 (IR) 實務角度**，客觀評估 HostWitness 的實際幫助、優點與應改進之處。
評估依據：核心程式碼（Core / Providers / UI / Agent）、`ANALYST_BRIEF.md`、`docs/LIMITATIONS.md`、`docs/TECH_DEBT.md`。

> 與 `ANALYST_BRIEF.md`（對外定位／路線目標）互補：本文件偏向**內部誠實評估與優先級判斷**。

---

## 一句話定位

HostWitness 是一個 **Windows 主機事件回應的「第一現場分流站 + 證據交接點」**——填在 EDR 即時觀測與完整磁碟／記憶體鑑識之間。
它不是要取代 Autopsy / Velociraptor / KAPE / X-Ways，而是要**快速回答**：

- 這台機器**現在**在跑什麼？
- **最近**發生了什麼？
- 有沒有**持久化**？
- 哪些證據要**立即保全**？
- 要不要**升級到完整取證**？

以這個定位來看，它做得相當扎實。

---

## 優點（實務上真正有幫助的地方）

### 1. 單一介面整合多條鑑識線索
Live Process / TCP、ETW、Event Log、Browser、LNK、JumpList、Prefetch、Amcache、Offline Hive、MFT、Timeline 都在同一個 WPF 工作區，並能從一個 PID 直接 pivot 進 Timeline 做關聯。
實務上「下載 → 開啟 → 執行 → 使用者互動」這條鏈能在一個視窗裡拼出來，對到場分流很省時間。

### 2. 證據可辯護性（最被低估的強項）
Snapshot 不是單純丟原始檔。它帶 `manifest.json`：

- `modeProfile`、`preflight`
- `collectionSummary`（含 `wasEventCountCapped`、`etwDroppedEventTotal`、`uiBackpressureDroppedTotal`、artifact copy 完整度等）
- `knownLimitations`

也就是說，**它會誠實告訴你「這份證據是在什麼條件下採的、漏了多少」**。多數輕量工具不會記這些，對寫報告與法庭防禦很關鍵。

### 3. Snapshot 完整性把關
- 開啟前驗 `hashes.txt`
- `raw/` 證據參照被約束在 bundle 內，不向外解析
- export 採 atomic（`.partial` 工作目錄 → 完成才 `Move`），失敗不會吐出不一致的 hash 清單
- 驗證會列舉磁碟上所有檔案，**未宣告於 `hashes.txt` 的檔案會判 Failed**（防竄改 bundle 假性通過）

### 4. 採集順序符合現場慣例
MFT 走 **raw volume → Backup privilege → VSS fallback**；VSS 需 Admin 並有 pre-check（`IsRunningAsAdmin` / `IsVssServiceRunning`）。這是標準 DFIR 動作，代表設計者懂現場。

### 5. 鑑識 vs 分流邊界清楚
Forensic Strict / Triage Fast 分得明白；Live Registry 與 Create Dump 明確標記為 **non-forensic / high-risk**。知道哪些能當證據基線、哪些只是輔助，避免誤用。

### 6. 工程品質基線不差
- 197 個測試、套件弱點已清
- P/Invoke 用 `SafeHandle` 而非 `DangerousGetHandle`
- `RegistryLivePolicy` 這種「單一閘道」設計，避免 UI／Agent／manifest 各寫一套規則
- 技術債是**被記錄、被分級**的（`TECH_DEBT.md`），不是放著爛

---

## 應改進之處（按實務影響排序）

### P1 — 採集可信度儀表板 ✅ 已實作（2026-06-06）
原問題：資料其實都在 manifest 裡，但分析師要自己翻 JSON 才知道「這次掉了多少 ETW、event 是否被 cap、artifact 有沒有完整複製」。**這些 caveat 太容易被忽略，而忽略它們會直接導致誤判（「沒看到 ≠ 不存在」）。**

**實作內容：**
- **Core**：`WinDFIR.Core/Snapshot/CollectionTrustReport.cs` —— `CollectionTrustAssessor` 解析 `manifest.json`（`collectionSummary` / `preflight` / `complete` / `registryMode` / `modeProfile` / `knownLimitations`），對每個面向算出紅／黃／綠，整體取最差。可吃 `SnapshotIntegrityStatus` 一併納入。純 Core、可單元測試（13 個新測試）。
- **訊號**：Integrity、Bundle completeness、Event truncation、ETW completeness、UI render backpressure、Raw artifact copy、VSS artifact source、Preflight、Privilege、Registry mode、Mode profile。
- **判讀規則重點（讀過文件的細節）**：
  - `failedEvidenceReferenceCount > 0` → 紅（raw 證據遺失）；`skipped`/incomplete → 黃。
  - **UI backpressure 只標黃且明示「不影響已持久化的 timeline」**（依 `LIMITATIONS.md` §10a，它只影響即時畫面完整度）。
  - 欄位缺失 → `UNKNOWN`（聚合視為黃）：鑑識上「無法確認完整」不可讀作「完整」。
  - Integrity Failed → 直接拉成整體紅。
- **UI**：`WinDFIR.UI/Views/CollectionTrustWindow.xaml` —— 開啟 snapshot 後自動彈出（modal），含 RAG 橫幅、headline（host/tool/time/event 數）、訊號清單、known limitations、「Copy summary」。File 選單新增 **Collection Trust...** 可隨時重看。
- **附帶修正**：export 端 `etwDroppedEventTotal` 改為**一律寫入（含 0）**，讓「0 次」與「未記錄」可區分。

### P2 — 幾個高價值攻擊鏈 artifact 只有「登錄指標」級深度（進行中）
目前對以下多半只看到登錄路徑的存在性，未真正解析 repository / ESE / job db：

- **Scheduled Tasks XML** — ✅ **已實作（2026-06-06）**，見下。
- **WMI 事件訂閱**（`ROOT\subscription` 的 `__EventFilter` / `__EventConsumer` / `__FilterToConsumerBinding`）
- **BITS job database / 傳輸記錄**
- **SRUDB.dat**（ESE application/resource tables）
- **PowerShell ConsoleHost history** — ✅ **已實作（2026-06-06）**，見下。
- **BAM / DAM**（offline hive registry FILETIME）— ✅ **已實作（2026-06-06）**，見下。

這些恰恰是現代攻擊者常用的持久化與執行痕跡。**補這些的實務價值，遠高於再加一個泛用 parser。**

**已完成：Scheduled Tasks XML**
- **Parser**：`WinDFIR.Providers/Parsers/ScheduledTaskParser.cs` —— namespace-agnostic（用 LocalName 比對）解析 `%WinDir%\System32\Tasks` 下的 Task XML：RegistrationInfo（Date/Author/Description/URI）、Principals（UserId/GroupId/RunLevel/LogonType）、Triggers（型別/Enabled/StartBoundary）、Actions（Exec Command/Args/WorkingDir、ComHandler ClassId）、Settings（Enabled/Hidden）。
- **真實檔案編碼修正**：on-disk Task 檔是 UTF-16（帶 BOM、宣告 `encoding="utf-16"`）；用 `XDocument.Load(stream)` 走位元組流（字串多載則剝除 XML 宣告），避免 `XDocument.Parse(string)` 在 UTF-16 宣告上拋例外 —— 這是會讓真實檔案完全解析失敗的真 bug。
- **Provider**：`WinDFIR.Providers/ScheduledTaskProvider.cs` —— 一次性掃描（如 RecentLnk），每個 task 一筆 `Category=Persistence / Action=ScheduledTask` 事件，含 triggers/actions/principal、`ObjectFile`=目標 exe（rooted 時）可供 pivot；時間戳 = RegistrationDate ?? 最早 trigger ?? 檔案 mtime。Tasks 根目錄可注入（測試用），task 名以根相對路徑計算。註冊於 UI 與 Agent（`--providers=ScheduledTask`）。
- **Snapshot 連動**：`SnapshotExporter` 新增 `ScheduledTask` artifact 來源（複製進 `raw/tasks/`），所以不會被當成 skipped 而誤觸 P1 信任儀表板的黃燈。
- **測試**：`WinDFIR.Tests/ScheduledTaskTests.cs`（10 個：namespace 解析、ComHandler、malformed/錯 root、task 名推導、provider 端對端、exporter 複製）。
- **界線**：解析的是磁碟上的 Task XML（live 或經 VSS/offline 路徑），**非** registry `TaskCache`（`SOFTWARE\...\Schedule\TaskCache`）的二進位 SD/Dynamic info；後者可日後補。

**已完成：PowerShell ConsoleHost history**
- **Parser**：`WinDFIR.Providers/Parsers/PowerShellHistoryParser.cs` —— 解析 PSReadLine `ConsoleHost_history.txt`（UTF-8 純文字、每行一條已接受指令），每行一筆 entry，並做**保守的攻擊關鍵字偵測**（`-enc`、`IEX`、`DownloadString`、`Net.WebClient`、`hidden`、`bypass`、`certutil`、`FromBase64String` 等，substring/不分大小寫，明示為 heuristic triage 非證明）。
- **Provider**：`WinDFIR.Providers/PowerShellHistoryProvider.cs` —— 列舉所有使用者 profile（users 根可注入測試），每條指令一筆 `Category=PowerShell / Action=ConsoleHostHistory` 事件，`User` 由路徑推導，命中關鍵字寫入 `SuspiciousKeywords`。`Confidence=Medium`（history 記錄已接受指令，非成功執行之證明）。註冊於 UI 與 Agent（`--providers=PowerShellHistory`）。
- **Snapshot 連動**：`SnapshotExporter` 新增 `PowerShellHistory` 來源（複製進 `raw/powershell/`；多筆事件共用同一檔，依來源路徑去重，只複製一次）。
- **測試**：`WinDFIR.Tests/PowerShellHistoryTests.cs`（5 個：行切分/空白/關鍵字、空內容、provider 端對端、exporter 去重複製）。
- **界線**：格式**無 per-line 時間戳**，所有事件以檔案 mtime 為時間錨；**多行指令**被 PSReadLine 以多個實體行儲存且無逸出，無法可靠重組 —— 刻意以「每實體行一筆」處理並文件化（盲猜邊界更糟）。

**已完成：BAM / DAM（每使用者最後執行時間）**
- **Parser**：`WinDFIR.Providers/Parsers/BamDamParser.cs` —— 解碼 `SYSTEM\...\Services\bam|dam\State\UserSettings\<SID>`（含舊版無 `State`）下，每個 value（名稱=執行檔 NT 路徑、資料前 8 bytes=FILETIME）的**最後執行時間**；對 bookkeeping 值（Version/SequenceNumber）、過短/零/超界時間做防呆。
- **整合方式（無新增重疊查詢、零重複事件）**：BAM/DAM 鍵位於 `Services` 子樹下，**已被既有遞迴 Services 查詢列舉**。在 `OfflineHiveRegistryProvider.BuildOfflineRegistryValueEvents` 以**路徑觸發**就地 enrich（仿 UserAssist/ShimCache decode pattern），加上 `OfflineHiveDecoded=BAM/DAM`、`BamExecutablePath`、`BamUserSid`、`BamLastExecutionUtc`，並把事件 **Timestamp 錨定在實際執行時間**（非鍵的 LastWrite），讓它在 timeline 落在正確時點。
- **測試**：`WinDFIR.Tests/BamDamTests.cs`（12：鍵偵測、FILETIME 解碼、防呆、就地 enrich 與時間錨定、非 BAM 值不誤判）。
- **界線**：BAM 提供「**最後一次**執行時間 + 執行檔路徑（NT device 路徑，未轉碟符）」，非執行次數、非完整歷程；Win11/部分版本 BAM 行為有差異，仍應與 Prefetch/Amcache/UserAssist 互相佐證。

### P3 — JumpList DestList parser 目前「失效但安全」
其 StreamName／offset 格式與磁碟上真實 DestList 佈局不符，查找永遠 miss、安全回退到 LNK 時間戳。
意即 **JumpList 的「上次存取時間」這條線索目前實質拿不到**。

這是**刻意延後**的決策（盲改 offset 會注入錯誤時間戳，更糟）。正確修法需以真實 `AutomaticDestinations` 樣本驗證 v1/v3/v4 變長結構。對重度依賴 JumpList 時序的案子，這是已知盲點。

### P4 — 規模上限要更醒目
- in-memory index 有 `Index.MaxEvents` cap（可設）
- export / session 單次 **500k event** cap
- 單次 raw read / MFT load **100 MB** cap
- 無跨 volume 合併 MFT 檢視（跨碟比對需手動切 tab）

在吵雜主機上會 truncate。MFT 已有 `PARTIAL / CAPPED` 前綴，但整體「我看到的是不是全部」需要更強的視覺警示（與 P1 同源）。

### P5 — Remote Agent 尚非成品
Agent 存在但在 stable scope 之外：集中 intake、retry/resume、簽章、版本治理、回傳驗證都還沒到位。
**多機事件目前撐不起來**，實務上只能單機跑。要做真正的大規模 IR，這塊得產品化。

### P6 — 根本性盲點（live tool 宿命，非 bug）
測不到：kernel rootkit、firmware 持久化、hypervisor 攻擊、進階隱藏進程；執行工具本身就會改變記憶體狀態。
文件已誠實列出，但這意味著**面對高階對手，它永遠只能是證據源「之一」，不能是唯一**。長期應補 direct EVTX / VSS copy、更多 raw parser，降低對 live API 的依賴。

---

## 適用性對照

| 適合 | 不適合 |
|------|--------|
| 到場初判、scoping、決定要不要升級取證 | 對抗 kernel／firmware 級進階對手的唯一工具 |
| 常見惡意軟體 / LOLBin 的持久化獵捕 | 大規模多機事件（Remote Agent 未成熟） |
| 需要**可辯護、帶 metadata** 的證據交接 | 需 BITS／WMI 訂閱／SRUM 深度解析的案子 |
| 單機快速時間軸關聯 | 完整 NTFS deep dive（用 KAPE / Autopsy / X-Ways） |

---

## 結論

定位清楚、誠實標示限制、證據可辯護性是亮點，工程品質基線穩。

**最值得投資的下一步：**

1. **採集可信度儀表板（P1）** — 降低誤判風險，成本低、收益直接。
2. **補齊 WMI／BITS／SRUM／Scheduled Task 深度（P2）** — 直接擴大能破的案子類型。

兩者投報率都明顯高於繼續打磨已經夠用的部分。

---

*文件建立：2026-06-06。後續變更請同步更新本表頭日期，並與 `ANALYST_BRIEF.md`、`docs/LIMITATIONS.md`、`docs/TECH_DEBT.md` 對齊，避免敘述漂移。*
