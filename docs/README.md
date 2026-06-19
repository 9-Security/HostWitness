# HostWitness 說明文件

本目錄集中存放專案相關說明與記錄；根目錄以 README、規格書、發布腳本與正式輸出為主。

> **注意**：過時說明已整併至 `待修復問題記錄.md`；原歷史修復記錄（old_docs）已移除以精簡目錄，可自版控歷史還原。

## 常用（當前狀態）

- **使用說明.md** — HostWitness 主程式使用說明（安裝、介面、分頁、常用操作、設定與參考）
- **待修復問題記錄.md** — 已知問題與後續修復方向
- **專案進度與目錄說明.md** — 專案進度摘要與目錄結構（含已移除冗餘項目）
- **LIMITATIONS.md** — 取證限制與即時採集風險說明
- **VERIFY_AND_SMARTSCREEN.md** — (EN) 下載發行版後的 SmartScreen 警告處理與 SHA256 驗證
- **EventLogView說明.md** — Event Log View 使用說明
- **PrefetchView說明.md** — Prefetch View 使用說明
- **AmcacheView說明.md** — Amcache View 使用說明
- **UI驗證說明.md** — 介面驗證與檢查重點
- **ARCHITECTURE.md** — 架構概覽
- **TECH_DEBT.md** — 技術債與過渡計畫（Registry、UI 解耦、Index 持久化、Docking）；§3 含 **Index 完整持久化決策框架**（開工前目標／canonical store／版本與 migration）；開頭「**中長期排程**」為優先順序與建議處理方式
- **開發者說明.md** — 架構概觀、新增 View/ViewModel 與分頁步驟、Provider 註冊
- **穩定版路線圖.md** — 穩定版成熟度強化的階段、交付物與 release gate
- **穩定版邊界定義.md** — 目前 stable baseline 的平台、模式、輸出與 high-risk 功能邊界
- **StableReleaseVerificationMatrix.md** - manual verification matrix and release gate criteria for the current UI/Core stable scope
- **StableReleaseChecklist.md** - stable release sign-off checklist for automated and manual verification
- **StableReleaseVerificationRecordTemplate.md** - fill-in template for storing the release verification evidence
- **StableReleaseManualVerificationRunbook.md** - operator runbook for executing matrix rows and recording Pass/Fail/Blocked/N/A with blocker ownership
- **StableOperatorProfileAndSOP.md** - operator profiles (Forensic Strict / Triage Fast), SOP runbook, UI action classification (forensic / non-forensic / advanced), high-risk isolation
- **Agent工作協議.md** — 後續使用 agent / coding assistant 時的 anti-drift 任務格式與工作邊界
- **CursorStableHandoff.md** - direct handoff note for completing the remaining stable manual verification and sign-off workflow in Cursor
- **FORENSIC_ASSUMPTIONS.md** — 鑑識假設與限制
- **RawDisk使用說明.md** — Raw Disk 讀取與 AddRawHive API 使用（離線 Hive 進階）
- **MFT收集流程說明.md** — MFT 檔案/磁碟載入流程、狀態列提示與 per-source tab 行為
- **Remote Agent guide** - HostWitness.Agent console usage and deployment notes (kept in repo; out of current stable scope)
- **還有什麼要做的事項.md** — 目前 stable 收尾、原可選功能實作狀態與未來 hardening 項目彙整

## 規格

- **single_host_live_forensics_activity_correlation_tool_final_development_specification.md**（專案根目錄）— 開發規格與設計原則。歷史驗證報告（若有）可自版控還原。

## 編譯與發布

- 發布輸出在專案目錄下 **`Release\`**
- 執行根目錄的 **`publish.cmd`**（建議 `cmd.exe /d /c .\publish.cmd`）可產生 `Release\HostWitness.exe`
- `publish.cmd` 與 stable gate 會以 repo-local **`.nuget-appdata\NuGet`** 隔離 NuGet 設定，避免使用者 `%AppData%\NuGet` 的損壞設定影響官方建置/發布路徑
- **單檔發布驗證**：`dotnet publish WinDFIR.UI\WinDFIR.UI.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o Release_SingleFile` 可產出單一 exe，WPF 已驗證可正常啟動。
- 最新編譯時間與驗證步驟請參考 `UI驗證說明.md`
- **Stable gate**: run `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1` before a stable publish; the gate internally calls **`publish.cmd -SkipPublish`** via cmd.exe and writes retained JSON reports to `Release_Verification\`.
- **保留輸出**：`Release\` 為正式發布輸出；`Release_Verification\` 僅保留正式 release 與最新 workspace 驗證報告。
- **不需閱讀的暫存輸出**：`Release_publish_probe\` 與根目錄 publish/build 診斷 log 為暫時除錯產物，已從目錄清理並加入忽略。
- **settings.json** 位置：`%AppData%\HostWitness\settings.json`
  - PID 快取：TTL/LRU/長駐清單（EventLog、ETW）
  - **Index.MaxEvents**：Activity Index 最大事件數（0=無上限）
  - Ui：字型大小、**TimeZoneDisplay**（Local/UTC）、**RegistryUseOfflineOnly**（僅離線登錄檔）、**ExportDefaultDirectory**（匯出預設路徑）、**ShowStatusBarDiagnostics**（狀態列診斷顯示）
- Advanced → Settings：PID 快取、字型、TimeZone、**Registry（僅離線登錄檔）**、**Raw Disk (Offline Hive)**、**Export 預設路徑**、Activity Index Max events、**ETW Throttle cap**；Registry 區塊含說明連結
- **VSS**：Offline Hive 與 Snapshot 匯出支援 Volume Shadow Copy；需**管理員**且 **Volume Shadow Copy 服務**運行，失敗時自動回退即時路徑。詳見 `LIMITATIONS.md` §8
- **Live Process Create Dump**：右鍵 Create Dump > Create minidump / Create full dump（單一程序 minidump；dump 其他程序需管理員）。Show process tree 時 TreeView 亦有相同右鍵選單。詳見 `LIMITATIONS.md` §12
- **UI Drill-down**：在 Live Process 選中一行程式後，雙擊列或右鍵「Show related events (Drill down)」會切換至 Timeline 並僅顯示該 Process 的 File/Registry/Network 等事件；Advanced → Drill Down 選單在 Live Process 分頁時啟用；Timeline 顯示「Filtered by PID: xxx」與「Clear process filter」可還原全量時間軸。
- **靜態分析 Autorun**：靜態分頁新增 **Autorun**，對齊 Sysinternals Autoruns 的 Registry 開機／登入 Run 檢視（Run、RunOnce、User Run、User RunOnce、RunServices、RunServicesOnce、PoliciesRun、StartupApprovedRun、Winlogon、IFEO）；資料來自 Offline Hive，可依 Location 篩選與搜尋；**Entry Details 選取在 Refresh 後保留**（不再幾秒自動清空）。
- **改完 code 自動編譯發布**：`.cursor/rules.md` §13「Build & Release (After Code Changes)」；改完 code 後執行 **`.\publish.cmd`** 產生 `Release\HostWitness.exe`。
- **程式碼簽章**：`.\create-signing-cert.ps1` 建立自簽憑證（Subject 含 CN/O/OU/L/S/C/E=shar@nine-security.com，純 ASCII 避免簽章詳細資訊亂碼）；**`publish.cmd`** 於發布完成後呼叫 **`scripts\Sign-HostWitness.ps1`** 以 signtool 簽章 exe（/d、/du、/tr 時間戳記、/td SHA256；`$env:HOSTWITNESS_SIGNING_URL`、`$env:HOSTWITNESS_TIMESTAMP_URL` 可覆寫）。exe 屬性：Product HostWitness、Company nine-security Inc.、Copyright (c) 2026 nine-security Inc.。
- **Detach/Restore 與工具列**：由 MainViewModel 驅動（ToolbarViewType、IsDetachRestoreMode、DetachButtonToolTip）；視圖與 Detach 狀態由 **ViewRegistryService** 持有，利於未來 Docking 替換；分離後浮動視窗工具列與主視窗一致。
- **Session 還原**：關閉時若有事件則儲存至 `%AppData%\HostWitness\last_session`；**若時間軸為空則清除**已存 session，避免還原過期資料；**File → Save session now** 可手動儲存。
- **版面儲存/還原**：關閉時自動儲存主視窗與 detached 視窗位置/大小、分頁選中至 `%AppData%\HostWitness\layout.json`（**`APPDATA` 環境變數**若已設定則優先作為基底路徑）；**File → Save layout now** 手動儲存、**Restore default layout** 還原預設。
- **Open Snapshot**：多個 `snapshot_*` 時以路徑排序後載入第一個含 `timeline.json` 者；離線事件中的 ProcessKey／NetworkFlowKey 可正確還原。詳見 **使用說明.md**。
- **HostWitness.Agent**：ETW 選用；結束代碼 **1**（啟動失敗並 rollback，或採集停止成功但 Snapshot 匯出失敗）、**2**（採集停止階段失敗、不匯出；stderr 會標示 export skipped）。詳見 **遠端採集Agent說明.md**。
- **SQLite 匯出/開啟**：**File → Export to SQLite…** 將目前時間軸匯出為 .db；**Open from SQLite…** 以分頁載入大型 DB。SQLite schema 含 version/migration（舊 DB 會補 category 欄位與索引）。
- **Raw Disk**：Settings → Raw Disk (Offline Hive) 可設定磁碟/offset/size 供離線 Hive；MFT 分頁改為 **Load from Volumes...**，可一次選多個磁區並為每個來源建立獨立 tab；每槽依序嘗試 raw volume、Backup privilege 直接讀取、最後才用 VSS 快照讀取 `$MFT`（需管理員），且已移除會重跑來源的 **Refresh**。`Load MFT file...` 會自動偵測 1024 / 4096-byte record size；若磁碟型來源命中 100 MB 讀取上限，tab 狀態列會警告結果可能不完整。
- **遠端採集腳本**：`scripts/ScheduledCollect.ps1`、`scripts/RunCollectAndCopy.ps1` 提供排程與自動回傳範例；見 `遠端採集Agent說明.md`。
- **遠端狀態回報**：回傳腳本會輸出 `collect-status-latest.json`（success/stage/error、可選 `agentExitCode` / `exportSkipped`、`manifestVerified`），可加 `-StatusHmacKey` 產生 HMAC 簽章。`RunCollectAndCopy.ps1` 搭配 HostWitness.Agent：`0` 成功；`1` 為 Agent `1`、複製／驗證失敗等；`2` 為 Agent `2`（採集器停止失敗、未匯出 snapshot）。
- **Registry Search**：Advanced → Registry Search… 僅在啟用「允許 Live Registry（實驗性）」時可執行 Live 查詢；預設 policy disabled，建議鑑識使用 Offline Hive。
- **Settings Profiles**：Settings 視窗可一鍵套用 `Forensic Strict` / `Triage Fast`，並支援 Profile JSON 匯入/匯出。
- **Timeline 匯出**：建議檔名含日期範圍、匯出前 N 筆/空結果/錯誤提示；Settings 可設匯出預設路徑。
- **UI 背壓可視化**：狀態列新增 **UI Queue**（pending / dropped total），在高頻事件時可觀察 UI 佇列壓力與丟棄量。
- **啟動預設分頁**：程式啟動時預設選中 **System Info**（靜態分頁），動態分頁不預選。

---

## 後續待辦與可選項目

**必須先做**項目多已完成；其餘待辦、可選功能與已知風險彙整於：

- **還有什麼要做的事項.md** — 可選功能與 Collector 強化摘要、已知風險、建議修復
- **待修復問題記錄.md** — 已知問題與修復方向、下階段待辦

---

**文件更新：** 2026-03-20（Session 空載清除、`layout.json` 與 `APPDATA`、Snapshot 多 bundle 選取與離線身分鍵、Agent 結束代碼；條列見上文）；2026-03-19（新增 `穩定版邊界定義.md`，凍結目前 stable baseline 的平台、模式、輸出與 high-risk 邊界）；2026-03-19（新增 `穩定版路線圖.md` 與 `Agent工作協議.md`，補上穩定版成熟度與 agent anti-drift 入口）；2026-03-19（同步移除確認未使用的 MFT private wrapper 與 `MainWindow` 中未讀取的 MFT view accessor）；2026-03-19（同步 MFT 檔案載入自動偵測 1024 / 4096-byte record size、100 MB 截斷警告與舊時間戳保留）；2026-03-18（同步 MFT per-source tabs、`Load from Volumes...` 多選與移除 Refresh）；2026-03-14（同步 MFT Raw run list 載入行為）；2026-03-13（移除不存在之驗證報告引用、精簡後續待辦為索引）




