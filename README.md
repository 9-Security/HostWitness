# HostWitness - Windows Live Forensics & Activity Correlation Tool

## 下載後：SmartScreen 警告與檔案驗證

> 這一段是給**下載發行版的人**看的。若你是自行建置，請見下方〈編譯與執行〉。

### 為什麼會跳出「Windows 已保護您的電腦」（SmartScreen）

HostWitness 是**開源的單機取證工具**，目前**未採用 EV 程式碼簽章憑證**。Windows SmartScreen 會對「下載量低／發行者信譽尚未建立」的程式顯示警告——**這是預期行為，不代表檔案損毀或含惡意程式**。取證工具因為會讀取原始磁碟（`\\.\PhysicalDrive0`）、dump 行程記憶體、操作特權，比一般程式**更容易**被 SmartScreen 或防毒軟體標記。

要繼續執行：

1. **先用下方 SHA256 驗證檔案**，確認你下載到的就是官方發行版本（這比點掉警告更重要）。
2. 驗證通過後，在 SmartScreen 畫面點 **「其他資訊」→「仍要執行」**；
   或在檔案上按右鍵 → **內容** → 勾選 **解除封鎖（Unblock）** → 確定。

> 不想看到此警告、且想以你自己的名義簽章，最便宜的公開路線是 **Certum 開源程式碼簽章憑證（約 US$50/年，雲端簽章免實體 token）**；唯有 **EV 憑證（約 US$249/年）** 能立即通過 SmartScreen。在累積足夠下載信譽前，OV 等級憑證仍會跳警告。

### 用 SHA256 驗證檔案完整性

下載後、執行前，用 PowerShell 計算雜湊：

```powershell
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

把輸出的 `Hash` 值與**該版本發行說明**中公布的 SHA256 比對（每個版本記錄於 `docs\RELEASE_NOTES_<版本>.md`，例如 1.3.0 見 `docs\RELEASE_NOTES_1.3.0.md` 的〈Verifying the download〉；GitHub Release 頁的資產說明亦會附同一雜湊）。

一行自動比對（把 `<EXPECTED_SHA256>` 換成公布值）：

```powershell
if ((Get-FileHash .\HostWitness.exe -Algorithm SHA256).Hash -eq '<EXPECTED_SHA256>') { 'OK：雜湊相符' } else { '警告：雜湊不符，請勿執行' }
```

雜湊**不相符**代表檔案在傳輸中損毀、或不是官方發行版本——**請勿執行**，重新自官方來源下載。

### 最高信任：自行從原始碼建置

不信任任何預編譯的二進位檔時，最可靠的做法是自行建置：clone 本倉庫後執行 `cmd.exe /d /c .\publish.cmd`，產出 `Release\HostWitness.exe`（self-contained 單檔，詳見下方〈編譯與執行〉）。本專案開源即為此——程式碼可稽核、建置可重現。

## 編譯與執行

- **發布腳本**：在專案目錄執行 **`publish.cmd`**（由 **cmd.exe** 呼叫 `dotnet`，官方唯一支援的一鍵發布路徑）：`dotnet restore` → build → **單元測試** → **publish**；預設產出 **`Release\HostWitness.exe` 單一檔案** (**self-contained** + **win-x64**)。`publish.ps1` 已停用，請改用 `publish.cmd`。
- **啟動**：`啟動HostWitness.bat`（一般權限）或 `啟動HostWitness(管理員).bat`（管理員權限），皆從 `Release\HostWitness.exe` 啟動。
- **說明文件**：規格與其他說明在 `docs\` 目錄。
- **Stable scope note**: the current stable update focuses on the local UI/Core workflow; `HostWitness.Agent` remains in the repo as a future update item and is not part of the current release gate.

```bat
REM Publish (default: self-contained single-file win-x64 -> Release\HostWitness.exe)
cmd.exe /d /c .\publish.cmd

REM Windows on ARM64
cmd.exe /d /c .\publish.cmd -Runtime win-arm64

REM Smaller output; target needs .NET 8 Desktop Runtime
cmd.exe /d /c .\publish.cmd -FrameworkDependent

REM Build + test only (no Release\ output)
cmd.exe /d /c .\publish.cmd -SkipPublish

REM Stable gate then publish
cmd.exe /d /c .\publish.cmd -StableGate
```

- **publish.cmd troubleshooting**: `InvokeStableReleaseGate.ps1` runs **`publish.cmd -SkipPublish`** via cmd.exe. `publish.cmd` and the stable gate isolate NuGet state under **`.nuget-appdata\NuGet`** so broken user `%AppData%\NuGet` config does not affect the official release path. If restore/build/test still fails, check the **publish.cmd: ERROR** step name first; file-lock failures usually mean `HostWitness.exe`, `Release\`, or `bin\Release` is in use. If the local wrapper cache looks corrupted, remove `.nuget-appdata\` and rerun `cmd.exe /d /c .\publish.cmd`.

手動等同預設發布：`dotnet publish WinDFIR.UI\WinDFIR.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=none -o Release`

**程式碼簽章（自簽憑證，公司：nine-security Inc.）**
- 首次：執行 `.\create-signing-cert.ps1` 建立自簽程式碼簽章憑證。Subject 含 CN=nine-security Inc.、O=nine-security Inc.、OU=Nine-Security、L=Taipei、S=Taiwan、C=TW、E=shar@nine-security.com；匯出至 `certs\HostWitness.pfx`（預設密碼 `HostWitness`）。Subject 使用純 ASCII（L=Taipei、S=Taiwan）以避免簽章詳細資訊出現亂碼。
- 發布時：若存在 `certs\HostWitness.pfx`，`publish.cmd` 會呼叫 `scripts\Sign-HostWitness.ps1` 以 signtool 簽章 `Release\HostWitness.exe`（/d、/du、**/tr** 時間戳記）。密碼可設 `$env:HOSTWITNESS_PFX_PASSWORD`；描述 URL 可設 `$env:HOSTWITNESS_SIGNING_URL`（預設 `https://github.com/nine-security/hostwitness`）；時間戳記伺服器可設 `$env:HOSTWITNESS_TIMESTAMP_URL`（預設 `http://timestamp.digicert.com`）。無憑證或簽章失敗時仍會完成發布，主控台會提示 exe 可能未簽章。
- 簽章容錯：`Sign-HostWitness.ps1` 依序嘗試 RFC3161 (`/tr`)、舊式時間戳 (`/t`)、無時間戳。**dotnet publish** 失敗時 `publish.cmd` 以非零結束；僅簽章步驟失敗不讓 `publish.cmd` 失敗。
- 自簽憑證驗證：`Get-AuthenticodeSignature` 可能顯示 `UnknownError`（root not trusted），這代表簽章存在但本機信任鏈未建立；若要顯示為 `Valid`，需將自簽根憑證匯入受信任根憑證存放區。
- 需安裝 **Windows SDK**（含 signtool.exe）。憑證與 PFX 勿提交版控（`.gitignore` 已含 `certs/`、`*.pfx`）。

## 發布前檢查清單

改完 code 後建議依下列步驟再發布，以確保穩定與輸出正確：

1. **建置與測試**：執行 `.\publish.cmd`（或 `cmd.exe /d /c .\publish.cmd`）。腳本會依序：restore → Build → **單元測試** → **Publish**（self-contained 單檔），並以 repo-local **`.nuget-appdata\NuGet`** 隔離 NuGet 設定。若測試失敗會中止，不會產生完整 Release 輸出。
2. **手動驗證（建議）**：以**管理員**與**一般使用者**各執行一次：啟動程式 → 採集約 30 秒 → 匯出 Snapshot → 關閉程式。確認無崩潰且匯出可完成。
3. **Manifest checks**: verify that `manifest.json` includes `modeProfile`, `preflight` (`executionContext`, `isAdministrator`, `vssServiceRunning`, `timeZoneDisplay`, `enabledProviders`, `outputDirectoryWritable`, `availableFreeSpaceBytes`, `warnings`, `errors`; Agent may also include `collectSeconds`), `collectionSummary` (`sourceEventCount`, `exportedEventCount`, `eventCap`, `wasEventCountCapped`, `evidenceReferenceCount`, `rewrittenEvidenceReferenceCount`, `copiedArtifactFileCount`, `skippedEvidenceReferenceCount`, `failedEvidenceReferenceCount`, `wasArtifactCopyIncomplete`, `usedVssSnapshotForArtifactCopy`, `artifactCopyWarningCount`, `preflightWarningCount`, `preflightErrorCount`, `etwDroppedEventTotal`, `uiBackpressureDroppedTotal`), `knownLimitations`, `registryMode` / `registryLiveEnabled`, and `etwTotalDrops` (when throttling occurred).
4. **Stable gate**: run `powershell -ExecutionPolicy Bypass -File .\scripts\InvokeStableReleaseGate.ps1` before a stable publish. The gate runs **`publish.cmd -SkipPublish`** (via cmd.exe) for build+test, also under repo-local **`.nuget-appdata\NuGet`**, writes JSON reports to `Release_Verification\`, and checks required release docs. One-step gate+publish: `cmd.exe /d /c .\publish.cmd -StableGate`.
目前釋出流程以 **`publish.cmd`** 與 `Release\` 為準；`publish.ps1` 已停用。舊的 beta 包裝腳本與 `release-package/` 已移除。目錄清理後，`Release_publish_probe\` 與根目錄 publish/build 診斷 log 不再保留作為閱讀來源；`Release_Verification\` 僅保留正式 release 與最新 workspace 驗證報告。

## Project Structure

```
HostWitness/
├── publish.cmd                 # Official build / test / publish entrypoint
├── Release/                    # Official published output
├── Release_Verification/       # Retained stable gate reports
├── WinDFIR.Core/               # Core contracts and utilities
│   ├── Entities/               # Entity keys and data contracts
│   ├── Normalization/          # Normalization utilities
│   ├── Index/                  # Indexing and correlation
│   └── Snapshot/               # Snapshot export
├── WinDFIR.Providers/          # Data providers
│   ├── ProviderLifecycleHelper.cs  # shared Start/Stop with rollback (UI + Agent)
│   ├── EventLogProvider.cs
│   ├── BrowserHistoryProvider.cs
│   ├── RecentLnkProvider.cs
│   ├── JumpListProvider.cs
│   ├── RegistrySearchProvider.cs
│   ├── OfflineHiveRegistryProvider.cs
│   └── ProcessMemoryDumper.cs
├── WinDFIR.UI/                 # WPF application
│   ├── MainWindow.xaml
│   ├── Views/                  # Timeline/Live/Static/Prefetch views
│   └── ViewModels/             # View models
└── WinDFIR.Tests/              # Unit tests
```

## 最新功能摘要

- **版面儲存/還原（含分離視窗）**：關閉時自動儲存主視窗位置、大小、分頁選中與 detached 視窗座標/大小至 `%AppData%\HostWitness\layout.json`（路徑：若處理序環境變數 `APPDATA` 已設定則優先使用，否則沿用系統 ApplicationData）；File → Save layout now / Restore default layout。
- **離線身分鍵與匯入**：`timeline.json` / session / SQLite 還原時，`subjectProcess`（`ProcessKey`）與 `objectNetworkFlow`（`NetworkFlowKey`）字串可正確還原（含 ISO 時間與 IPv6 端點中的 `:`）；程式與網路鑽取在離線模式下可一致對應。
- **上次 session**：關閉時若目前時間軸為空，會清除 `last_session` 的已存檔，避免下次啟動還原到過期證據。
- **Open Snapshot / 完整性檢查**：若父資料夾底下有多個 `snapshot_*` 子目錄，會以**不區分大小寫的路徑排序**後選取**第一個**含 `timeline.json` 者（行為確定、可重現）。
- **SQLite 匯出/開啟**：File → Export to SQLite… 將目前時間軸匯出為 .db；Open from SQLite… 自 .db 載入時間軸。
- **Snapshot 可攜性與背景化**：匯出 Snapshot 時會先複製可攜式 raw 證據到 `raw/`，並把 `timeline.json` 的 evidence reference 改寫為 bundle-local 路徑；Open Snapshot / Export to SQLite / Export Snapshot 皆改為背景執行，降低大型資料量時 UI freeze。
- **Raw Disk**：Settings → Raw Disk (Offline Hive) 可設定磁碟/offset/size 供離線 Hive（重啟套用）；MFT 分頁改為「Load from Volumes...」，可一次選多個 NTFS 磁區並為每個來源建立獨立 tab；每槽依序嘗試 raw volume、Backup privilege 直接讀取、必要時再用 VSS 快照來取得 NTFS `$MFT`（需管理員）；若磁碟型來源碰到 100 MB 讀取上限，tab 狀態列會明確警告結果可能不完整。
- **遠端採集腳本**：`scripts/ScheduledCollect.ps1`、`scripts/RunCollectAndCopy.ps1` 提供排程與自動回傳範例；見 `docs/遠端採集Agent說明.md`。
- **遠端狀態回報**：回傳腳本會在輸出目錄寫入 `collect-status-latest.json`（success/stage/error、可選 `agentExitCode` / `exportSkipped`、`manifestVerified`），可加 `-StatusHmacKey` 產生 HMAC 簽章。`RunCollectAndCopy.ps1` 搭配 HostWitness.Agent：`0` 成功；`1` 為 Agent `1`、複製／驗證失敗等；`2` 為 Agent `2`（採集器停止失敗、未匯出 snapshot）。
- **UI Drill-down**：Live Process 選中程式後雙擊或右鍵「Show related events (Drill down)」→ 切換至 Timeline 並僅顯示該 Process 的 File/Registry/Network 等事件；Advanced → Drill Down 選單在 Live Process 分頁時啟用；Timeline「Filtered by PID」與「Clear process filter」可還原全量時間軸。
- **Detach/Restore 與工具列由 ViewModel 驅動**：主視窗 Detach/Restore 按鈕的圖示與 ToolTip 由 MainViewModel.UpdateDetachState 驅動；工具列按鈕顯示由 ToolbarViewType 綁定驅動。分離後的浮動視窗工具列與主視窗一致（圖示按鈕）。
- **啟動預設分頁**：程式啟動時預設選中 **System Info**（靜態分頁），動態分頁不預選。
- **Registry Search UI**：Advanced → Registry Search… 僅在啟用 Live Registry（實驗性）時可執行 Live 查詢；預設為停用並提示改用 Offline Hive。
- **Settings Profiles**：提供 `Forensic Strict` / `Triage Fast` 一鍵套用，並支援 Profile JSON 匯入/匯出。
- **Registry 鑑識預設**：Settings → Registry（鑑識）預設「僅離線登錄檔」。若要 Live Registry，需同時關閉 offline-only 並勾選「允許 Live Registry（實驗性，non-forensic）」。
- **Timeline 匯出與 UX**：匯出 CSV/JSON 建議檔名含日期範圍（timeline_yyyy-MM-dd_yyyy-MM-dd）；CSV 首行／JSON meta 可標註時間範圍；Settings 可設「Export default path」；匯出前 0 筆會提示、對話框標題顯示「將匯出 N 筆」、失敗時提示路徑/權限；篩選結果 0 筆時顯示「目前篩選結果為 0 筆」。
- **靜態分析 Autorun**：靜態分頁 **Autorun** 對齊 Sysinternals Autoruns 的 Registry 開機／登入 Run 檢視；Location 篩選與搜尋；Entry Details 選取在 Refresh 後保留。
- **改完 code 自動編譯發布**：改完 code 後執行 `.\publish.cmd` 產生 self-contained 單檔 `Release\HostWitness.exe`。
- **VSS 快照流程完善**：權限與 Volume Shadow Copy 服務預檢、WMI 錯誤碼可讀訊息、部分成功仍回傳 context、多磁碟區一致性警告。需管理員與 VSS 服務運行。
- **Activity Index 記憶體控管**：Settings 與 settings.json 可配置 Max events（0=無上限）；批次 eviction、TrimAllQueues、Evicted 提示。
- **Offline Hive 擴充 key**：SYSTEM／SOFTWARE／NTUSER／USRCLASS 高價值 key；鑑識優先 Offline Hive。
- **技術債**：短期維護債已於 2026-03-20 收斂；目前剩餘中長期項目主要為 Registry Live 下一個 major 是否移除、**Index 完整持久化**（canonical store / migration 尚未定案）與**完整 Docking UI**。`ShellExecute` UI hardening 已由 `ShellLaunchHelper` 收斂。詳見 `docs\TECH_DEBT.md`。
- Live Process Procmon-like：Filter Bar、Columns 顯示管理、Capture 暫停、右鍵 Apply as Filter/Copy Value/Open Directory、**Create Dump > Create minidump / Create full dump**、Result 高亮；**Show process tree** 時 TreeView 亦有相同右鍵選單。
- Live Process 欄位補強：Parent PID / parent image (same WMI pass) / user + Owner SID / Integrity / Session / Image Path / Company / Hash / Authenticode (WinVerifyTrust cache-only URL retrieval flag, no revocation checks; publisher from embedded cert read locally) / Last Operation / Last Result。
- Live TCP View：動態分析區 TCPView 即時連線列表（Local/Remote Address+Port、Create Time、Module Name）。
- PID 快取狀態列與 settings.json 可調（TTL/LRU/長駐程序）；Advanced → Settings：PID 快取、全介面字型大小、**Activity Index Max events**。
- 靜態 Network View 改名為 Netstat；Amcache View：Amcache.hve 解析；啟動診斷：`%AppData%\HostWitness\logs\startup.log`。
- **Offline Hive 離線一致性**：事件欄位 OfflineHiveSource/ConsistencyScope/SnapshotTimeUtc；Mixed 時警告；VssSnapshotContext.CreationTimeUtc。
- **Offline Hive 解碼**：OfflineHiveRegistryProvider 於 UserAssist（ROT13與執行次數/時間）、AppCompatCache/ShimCache（多版本對照解析），以及離線持久化相關鍵值（Services、StartupApprovedRun、IFEO、Winlogon）可產生額外結構化欄位（如 OfflineHiveDecoded、ServiceImagePath、ServiceDescription、ServiceFailureCommand、ServiceRebootMessage、StartupApproved_State、IFEO_Debugger、Winlogon_Shell）。另以保守欄位涵蓋 **BITS**（…\CurrentVersion\BITS 登録快照，非完整作業隊列解碼）、**WMI**（…\WBEM\CIMOM 與 ControlSet*\Control\WMI\Security，非 __EventFilter/Consumer 綁定枚舉）、**SRUM**（…\SRUM 登録子樹；不解析 SRUDB.dat/ESE）；解析失敗或無法可靠解碼時僅留原始值事件。
- **Event Log 採集**：EventLogProvider 除 Security / System / Application 外，於本機可用時會嘗試讀取常見 IR Operational channel（PowerShell、WMI-Activity、Task Scheduler、Windows Defender、Sysmon）；channel 不存在、未啟用或無權限時略過，不中斷其他日誌。Security 稽核事件 ID 的 Category/Action 採保守標示（例如 4698–4701 為排程相關稽核，不標成服務啟停），利於時間軸解讀。
- **時區顯示**：Settings TimeZoneDisplay (Local/UTC)；Timeline Action Time 依設定；Settings 視窗可選。
- **DestList**：EntryId、StreamName/EntryId 對照、依 LastAccessTimeUtc 排序。**Prefetch**：支援 version 10/13/15/17/23/26/30/**31（Windows 11）**；空清單時顯示原因提示（權限/路徑/無 .pf）。
- **MFT 從磁碟載入**：MFT 分頁的 **Load from Volumes...** 可一次選擇多個磁碟機（C:、D: 等），每個來源都會開成自己的 tab，不再把不同槽位資料混在同一張表。每個 volume tab 都會依序嘗試 **raw volume**、**Backup privilege 直接讀取 `$MFT`**、**VSS snapshot** 三條路徑；若失敗，請確認該磁碟機是本機已解鎖的 NTFS 分割區，或改用 **Load MFT file...** 載入事先匯出的 `$MFT`。`Load MFT file...` 會自動偵測常見 MFT record size（1024 / 4096 bytes），偵測結果會附在狀態列；若磁碟型來源在讀取時碰到 100 MB 上限，狀態列也會警告結果可能不完整。**MFT 分頁**：每個 tab 皆為單頁預設 500 筆，可選每頁 100／250／500／1000／2000／5000，支援第一頁／上一頁／下一頁／最後一頁與輸入頁碼跳頁；**Refresh 已移除**，若要重讀來源請重新執行載入；匯出 CSV/JSON 僅針對目前選中的 tab。
- **Help → About**：含著作權「Copyright © 2026 nine-security Inc. All rights reserved.」
- **Code review 與潛在 bug 修正**：Sqlite 版本統一（8.0.11）、Raw 讀取上限（100 MB，超限時會於 MFT 狀態列提示可能不完整）、匯出/儲存筆數上限（50 萬）、Snapshot evidence 會改寫為 bundle-local `raw/` 路徑、Open Snapshot / Export to SQLite / Export Snapshot 改為背景載入或匯出、Session 還原會提示目前 live index 容量上限、空 catch 改為 Debug.WriteLine、關閉視窗時 Dispatcher 防護（避免 InvalidOperationException）、`Load MFT file...` 自動偵測 1024 / 4096-byte record size，並保留 1980 年前的 NTFS 時間戳供 Time-stomp? 比對、移除確認未使用的 MFT wrapper 與 `MainWindow` 中未讀取的 MFT view accessor，且 UI / Agent 匯出 Snapshot 時會把 `modeProfile`、`preflight`（如 `executionContext`、`isAdministrator`、`vssServiceRunning`、`timeZoneDisplay`、`enabledProviders`；Agent 另含 `collectSeconds`）與 `collectionSummary`（如 `sourceEventCount`、`exportedEventCount`、`eventCap`、`wasEventCountCapped`）寫入 `manifest.json`。**HostWitness.Agent**：ETW 為選用（`--etw` 或 `--providers=…,ETW`）；啟動失敗會 rollback 已啟動之採集器並結束代碼 `1`；採集停止皆成功後若 Snapshot **匯出** 失敗亦為結束代碼 `1`（stderr：`Export failed: …`）；採集結束後若任一 `StopAsync` 失敗則**不匯出** Snapshot、stderr 標示 export skipped，並結束代碼 `2`。`BrowserHistoryProvider` 列舉使用者設定檔目錄時遇存取拒絕會記錄警告並略過，不中斷整支採集器。
- Offline Hive 支援 VSS 快照路徑（失敗時回退 Live）；InMemoryActivityIndex 有界佇列與 Evicted 提示；ETW 節流丟棄事件計數提示。 **High-volume pipeline:** `ETWMonitorProvider` uses a bounded ingest queue between the ETW callback and parsing/EventProduced; when full, drops are counted as `BurstQueue` in `etwDroppedEventTotal`. `MainWindow` drains the UI pending queue in bounded batches per dispatcher callback to reduce single-frame stalls while preserving `uiBackpressureDroppedTotal` semantics.

## M0 Milestone: Foundation

### What's Implemented

1. **Solution Structure**: Multi-project solution with clear separation of concerns
2. **Core Contracts**: 
   - Entity keys (ProcessKey, NetworkKey)
   - EvidenceRef for traceability
   - ActivityEvent as unified event contract
3. **Normalization Utilities**:
   - KeyGenerator for consistent entity identification
   - TimeNormalizer for time format conversions
4. **Index**: InMemoryActivityIndex for event storage and correlation
5. **Provider Pipeline**: IProvider interface with StubProvider implementation
6. **WPF UI**: Minimal shell with DataGrid showing ActivityEvents

### Building

```powershell
cd C:\Users\Sharlotlot\WinDFIR
dotnet build
```

### Running

```powershell
dotnet run --project WinDFIR.UI
```

### Testing

```powershell
dotnet test
```

## Next Steps

- **可延後**：AvalonDock 等完整停靠框架替換、更多遠端回傳編排（SIEM/工單整合）。
- **中長期技術債**：Registry Live 下一個 major 是否移除、Index 完整持久化、完整 Docking UI。
- 風險與緩解見 `docs\LIMITATIONS.md` §14；技術債見 `docs\ARCHITECTURE.md` §7。

**文件更新：** 2026-03-20（離線 `ProcessKey`/`NetworkFlowKey` 匯入還原、空時間軸關閉清除 `last_session`、版面路徑優先 `APPDATA`、多 `snapshot_*` 固定排序選取、Agent ETW 選用與結束代碼 1/2、採集器啟停 lifecycle、BrowserHistory 存取拒絕降級；短期技術債收斂摘要仍見下文歷史條目；並同步 `docs\使用說明.md`、`docs\遠端採集Agent說明.md`、`docs\開發者說明.md`、`docs\README.md`）；2026-03-19（同步 Snapshot manifest 新增 `modeProfile` / `preflight` / `collectionSummary` 欄位，涵蓋 UI 與 Agent 匯出）；2026-03-19（移除確認未使用的 MFT private wrapper 與 `MainWindow` 中未讀取的 MFT view accessor）；2026-03-19（專案狀態同步：文件與建置驗證一致，見 docs 目錄）；2026-03-19（同步 MFT 檔案載入的 1024 / 4096 record size 自動偵測、100 MB 截斷警告、pre-1980 timestamp 保留）；2026-03-19（移除舊 beta 發佈腳本、`release-package/` 與歷史根目錄筆記，文件引用收斂至 `docs\`）；2026-03-18（MFT 改為 per-source tabs；`Load from Volumes...` 可多選磁區；移除 Refresh，匯出僅針對目前 tab）；2026-03-17（MFT 磁碟載入改為 raw volume → Backup privilege → VSS ordered fallback，並加入非阻塞進度顯示）；2026-03-16（Snapshot evidence 改為 bundle-local `raw/` 路徑、Open Snapshot / Export to SQLite / Export Snapshot 背景化、Session 還原容量提示、dead code cleanup）；2026-03-14（MFT Raw 載入改為依 run list 重建邏輯資料流）；2026-03-13（專案狀態與文件整理）；待辦與已知問題見 `docs\待修復問題記錄.md`、`docs\還有什麼要做的事項.md`、`docs\TECH_DEBT.md`



