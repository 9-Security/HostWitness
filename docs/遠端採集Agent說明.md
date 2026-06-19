# 遠端採集 Agent 說明

## 概述

**HostWitness.Agent** 為無 UI 主控台程式，使用與 HostWitness 相同的採集管道（Providers），執行一段時間後將結果匯出為 Snapshot（timeline/entities/manifest/raw 等），供遠端部署與回傳使用。

> 範圍註記：`HostWitness.Agent` 目前保留於專案中，但不納入本輪 stable update 的完成條件；現階段穩定版驗收先以 UI / Core 本機流程為主。Agent 強化、遠端部署驗證與獨立採集 SOP 延後至未來更新項目。

## 建置與發布

```powershell
# 建置
dotnet build WinDFIR.sln --configuration Release
# 輸出：WinDFIR.Agent\bin\Release\net8.0-windows\HostWitness.Agent.exe

# 一鍵發布至 Release\Agent（建議遠端部署前使用）
powershell -ExecutionPolicy Bypass -File publish-agent.ps1

# 發布至自訂目錄
powershell -ExecutionPolicy Bypass -File publish-agent.ps1 -OutDir "C:\Deploy\Agent"
```

可另行 `dotnet publish WinDFIR.Agent\WinDFIR.Agent.csproj -c Release -o <path>` 至指定目錄。

## 使用方式

```text
HostWitness.Agent.exe [輸出目錄] [採集秒數] [--etw] [--providers=名稱1,名稱2,...]
                      [--evtx=檔案,...] [--srum=檔案,...] [--bits=檔案,...] [--wmi=檔案,...]
                      [--repo=路徑或URL] [--repo-token=金鑰]
```

- **輸出目錄**：Snapshot 匯出之路徑；若省略則使用目前目錄下 `AgentOutput`。
- **採集秒數**：採集執行秒數，預設 30；若省略則使用 30。
- **--etw**：啟用 ETW 監控（ETWMonitorProvider）。預設不啟用以減輕負載。
- **--providers=...**：僅啟用所列的採集器（逗號分隔、不區分大小寫）。若省略則啟用除 ETW 外之全部。
- **--evtx=...**：改用離線 `.evtx` 檔（逗號分隔）；指定後 EventLog 轉為**離線模式**，不讀本機頻道。
- **--srum=... / --bits=... / --wmi=...**：提供 `SRUDB.dat` / `qmgr.db` / `OBJECTS.DATA` 檔路徑以啟用對應的**選用**採集器（無預設路徑，需明確指定）。
- **--repo=...**：採集完成後將已驗證的 bundle 發布到中央 case repository；可為檔案系統路徑（本機 / UNC / 掛載）或 `http(s)://` intake 伺服器 URL。
- **--repo-token=...**：HTTP intake 需認證時的 bearer token（shared secret）。

**Provider 名稱（`--providers=` 用）**：`Process`（LiveProcess）、`Service`（LiveService）、`ProcessCrossCheck`（程序隱藏交叉比對）、`Net`（NetConnection）、`EventLog`、`RecentLnk`、`JumpList`、`BrowserHistory`、`ScheduledTask`、`PowerShellHistory`、`StartupFolder`、`Registry`（Live，需政策啟用）、`OfflineHive`、`ETW`。SRUM / BITS / WMI **不**走 `--providers=`，而是以上述 `--srum/--bits/--wmi` 檔案參數啟用。

### 結束代碼（process exit code）

| 代碼 | 意義 |
|------|------|
| **0** | 採集與停止皆正常，Snapshot 已匯出。 |
| **1** | 採集器**啟動**階段失敗（rollback）；**或** 採集與停止皆正常但 Snapshot **匯出** 失敗。上述情況皆**不會**產生有效本次 Snapshot。錯誤訊息於 stderr。 |
| **2** | 採集**停止**階段有失敗（任一 `StopAsync` 回報錯誤）；**不會**匯出 Snapshot（stderr 會標示 export skipped）。錯誤訊息於 stderr。 |
| **3** | 已使用 `--repo` 發布：Snapshot **匯出成功**，但發布到中央 repository 失敗（本機 bundle 仍完整無損，可手動回傳）。錯誤訊息於 stderr。 |

自動化腳本請以結束代碼判斷是否應回傳或重試；勿僅依主控台「Done」字樣判斷成功。

範例：

```powershell
# 輸出到 C:\Collect，採集 60 秒
.\HostWitness.Agent.exe C:\Collect 60

# 啟用 ETW 採集
.\HostWitness.Agent.exe C:\Collect 45 --etw

# 僅採集事件紀錄與網路連線
.\HostWitness.Agent.exe C:\Out --providers=EventLog,Net

# 採集 30 秒且含 ETW，輸出到當前 AgentOutput
.\HostWitness.Agent.exe --etw
```

結束後，輸出目錄內會有 `snapshot_yyyyMMdd_HHmmss` 子目錄，內含 timeline.json、entities.json、manifest.json、hashes.txt 及 raw\ 等。
`manifest.json` 會額外記錄 `modeProfile`、`preflight`（固定含 `executionContext = agent_headless`，並記錄 `isAdministrator`、`vssServiceRunning`、`useVssSnapshots`、`enabledProviders`、`collectSeconds`）、`collectionSummary`（如 `sourceEventCount`、`exportedEventCount`、`eventCap`、`wasEventCountCapped`）以及 `registryMode` / `registryLiveEnabled` 與 `knownLimitations`，便於回傳後判讀採集方式、截斷狀態與風險。

## 採集來源一覽

Agent 與 UI 共用**同一套 Providers**（`WinDFIR.Core` + `WinDFIR.Providers`），因此採集的不只 Windows 事件記錄，而是「事件記錄 + 即時執行狀態 + 持久化 / 使用痕跡 artifact」的完整 triage。預設啟用除 ETW 與三個選用資料庫（SRUM/BITS/WMI）外之全部；設定檔沿用 `%AppData%\HostWitness\settings.json`（若存在）。所有「離線」來源事件會帶 `Mode=Offline`。

> 以下內容由原始碼擷取（`WinDFIR.Providers\*.cs` 與 `Parsers\`），路徑 / 機碼均為程式實際讀取者。

### 啟用與權限總覽

| Provider（`--providers=` 鍵） | 採集內容 | 預設 | 需管理員 |
|---|---|---|---|
| `Process`（LiveProcess） | 執行中程序 + 生命週期 | ✅ | 部分（他人/SYSTEM 程序細節） |
| `Service`（LiveService） | 已安裝服務（WMI） | ✅ | 否 |
| `ProcessCrossCheck` | 程序隱藏交叉比對（tripwire） | ✅ | 否 |
| `Net`（NetConnection） | TCP 連線 + UDP 監聽 → PID | ✅ | 否 |
| `EventLog` | 事件記錄頻道 / 離線 `.evtx` | ✅ | **是**（Security 頻道） |
| `RecentLnk` | Recent 資料夾 `.lnk` | ✅ | 否（本使用者） |
| `JumpList` | Jump List 目的地 | ✅ | 否（本使用者） |
| `BrowserHistory` | 瀏覽器歷史 | ✅ | **是**（讀其他使用者） |
| `ScheduledTask` | 排程工作 XML | ✅ | **是** |
| `PowerShellHistory` | PSReadLine 歷史 | ✅ | **是**（讀所有使用者） |
| `StartupFolder` | Startup 資料夾持久化 | ✅ | **是**（完整列舉） |
| `OfflineHive` | 離線登錄檔 hive | ✅ | **是**（鎖定 hive 走 VSS） |
| `Registry`（RegistrySearch） | Live 登錄檔 autorun/MRU | ⚠️ 政策關閉 | 否（預設機碼） |
| `ETW`（ETWMonitor） | procmon 式即時活動 | ❌ `--etw` | **是** |
| SRUM（`--srum=`） | SRUDB.dat 使用歷史 | ❌ 選用 | 取得 live 檔需管理員+VSS |
| BITS（`--bits=`） | qmgr.db 下載作業 | ❌ 選用 | 取得 live 檔需管理員+VSS |
| WMI（`--wmi=`） | OBJECTS.DATA 訂閱持久化 | ❌ 選用 | 取得 live 檔需管理員+VSS |

### 各採集器詳細來源

#### 即時執行狀態

- **LiveProcess** — `Process.GetProcesses()` + WMI `Win32_Process`（每 5 秒重列；讀 PE 版本資訊並算映像 SHA-256）。欄位：ProcessName/PID/CommandLine/UserName/Integrity/ImagePath/Company/Hash/ParentPID + token/Authenticode。未提權時他人與 SYSTEM 程序的 CommandLine/路徑/parent 取不到（每程序失敗記入 `CollectionWarnings`）。
- **LiveService** — WMI `Win32_Service`（Name/DisplayName/PathName/State/StartMode/ServiceType/ProcessId/StartName）。為跨來源異常比對的「live 半邊」（hive 有、live 無 = 隱藏指標）。
- **ProcessCrossCheck** — `Process.GetProcesses()`（native）對比 WMI `Win32_Process`；差異會**重新查詢確認**後才報，過濾掉快照間啟動/結束的程序。「是 tripwire，不是保證」。
- **NetConnection** — `IPGlobalProperties` 取連線清單 + P/Invoke `GetExtendedTcpTable`/`GetExtendedUdpTable`（iphlpapi，AF_INET/INET6）做 PID 歸屬；每 3 秒。IPv6 scope id 刻意捨棄以對齊 PID-map 鍵。
- **ETWMonitor**（選用 `--etw`）— 訂閱四個 `Kernel-*/Operational` 頻道（Process/File/Network/Registry，**非**即時 kernel TraceEventSession）。每頻道受 `IsEnabled` 控制；有界佇列（預設 8192）滿則計丟（`BurstQueue`），分類節流（File 500/s、Registry 300/s、Network 300/s）。需管理員，預設關閉。

#### Windows 事件記錄（EventLog）

- **Live 頻道**：`Security`、`System`、`Application`，加 IR 常用 operational：`Microsoft-Windows-PowerShell/Operational`、`...-WMI-Activity/Operational`、`...-TaskScheduler/Operational`、`...-Windows Defender/Operational`、`...-Sysmon/Operational`（後備 `Sysmon/Operational`）。
- **離線**：以 `--evtx=` 註冊 `.evtx` 後**僅**讀這些檔、不碰 live 頻道（頻道名由檔名 `%4`→`/` 還原）。
- 每日誌上限 `MaxEventsPerLog=20000`、每筆屬性上限 20。已辨識的 EventID 含登入(4624/4625/…)、程序建立(4688)、服務安裝(4697)、排程(4698–4701 標為 `Query` 不誤標服務啟停)、PowerShell(4103/4104)、Sysmon(1/3/5/7/11/22/23) 等。讀 **Security 頻道需管理員**（拒絕存取會記警告續跑）。

#### 使用痕跡 / 持久化（檔案系統）

- **RecentLnk** — 僅 `%AppData%\Microsoft\Windows\Recent`（遞迴 `*.lnk`）。目標解析序：TargetPath → NetworkPath → RelativePath → **原始位元組 fallback**（regex 找 `C:\…`/UNC，UTF-16LE 後 ASCII；用 fallback 時信心降 Low）。存 `.lnk` 的 SHA-256。
- **JumpList** — `%AppData%\Microsoft\Windows\Recent\AutomaticDestinations\*.automaticDestinations-ms`（CFB/OLE，OpenMcdf）與 `…\CustomDestinations\*.customDestinations-ms`。AppId = 檔名首個 `-` 前綴。Automatic 串流以 LNK 解析並對應 `DestList`（MRU 序、存取次數、釘選、最後存取 FILETIME）。DestList parser 已對 v6 真實樣本 + JLECmd 驗證，拒絕版本 <1 或 >16 以免從垃圾捏造項目。
- **ScheduledTask** — `%WinDir%\System32\Tasks`（遞迴）。解析 task XML：作者/描述/URI/註冊時間/Principal/RunLevel/LogonType/Enabled/Hidden/Exec 命令與參數/觸發器/動作。**通常需管理員**。以位元組串流載入以正確處理 `encoding="utf-16"` BOM；存 task 檔 SHA-256。
- **PowerShellHistory** — 每位使用者的 `…\AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt`（讀所有使用者需管理員）。標記可疑 token（`-enc`、`frombase64string`、`downloadstring`、`iex`、`-w hidden`、`bypass`、`certutil`、`mimikatz` 等）。**無逐行時間戳**（以檔案 LastWrite 為錨）；信心 Medium（「記錄被接受的命令，非執行證明」）。
- **StartupFolder** — All-Users `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup` + 每位使用者 `…\AppData\Roaming\…\Startup`（完整列舉需管理員）。`.lnk` 解析 TargetPath/Arguments/WorkingDirectory；跳過 `desktop.ini`。
- **BrowserHistory** — Chromium 家族（Chrome/Chrome Beta/SxS、Edge/Beta/Dev/SxS、Brave/Beta/Nightly 的 `User Data\Default\History` 與 `Profile *\History`）、Opera（Stable/GX 的 `History`）、Firefox（`Mozilla\Firefox\Profiles\<profile>\places.sqlite`）。另**掃描所有使用者設定檔**（深度上限 8）並支援環境變數 `WINDFIR_BROWSER_HISTORY_PATHS` 覆寫。讀法：複製 DB(+`-wal`/`-shm`)到 `%TEMP%` 以 ReadOnly 開啟、每庫 `LIMIT 50000`。**讀其他使用者需管理員**。

#### 離線登錄檔

- **OfflineHive** — 預設四個 hive：`{Windows}\System32\config\SYSTEM`、`…\config\SOFTWARE`、`{UserProfile}\NTUSER.DAT`、`{UserProfile}\AppData\Local\Microsoft\Windows\USRCLASS.DAT`；另支援 `AddRawHive`（raw 實體磁碟讀取，需管理員）。Live 鎖定 hive 走 **VSS 快照**（不可用則回退 live 並警告，標 `ConsistencyScope` Single/Mixed）。解碼的機碼：
  - **SYSTEM**：Services（`ControlSet001/002\Services` 遞迴，含 **BAM/DAM** 每使用者最後執行 FILETIME）、MountedDevices、**ShimCache/AppCompatCache**、TimeZoneInformation、ComputerName、Select、WMI Security。
  - **SOFTWARE**：Run/RunOnce/RunServices(Once)、Policies\Explorer\Run、StartupApproved\Run、**Winlogon**、**IFEO**（遞迴）、Uninstall、AppCompatFlags\Layers、ProfileList、**BITS 登錄子樹**、**WMI CIMOM**、**SRUM 登錄子樹**、**TaskCache**（`Schedule\TaskCache\Tasks`，`Path` + `DynamicInfo` FILETIME；actions/triggers blob 不解碼）。
  - **NTUSER.DAT**：User Run/RunOnce、RunMRU、TypedURLs/TypedPaths、RecentDocs、**UserAssist**（ROT13 解碼名稱/執行次數/最後執行）、MountPoints2、OpenSave*MRU、LastVisitedPidlMRU、Streams。
  - **USRCLASS.DAT**：MuiCache、BagMRU、Bags。
  - 無專屬 parser 的二進位值以 **hex 截斷至 200 字元**呈現；所有 FILETIME 解碼都做範圍檢查避免捏造時間。
- **RegistrySearch**（Live，預設**關閉**，需 `RegistryLivePolicy` 政策啟用）— 預設僅四條查詢：HKCU/HKLM `…\CurrentVersion\Run`、HKCU `…\Explorer\RecentDocs`（遞迴）、HKCU `…\Explorer\RunMRU`。皆 HKCU 或世界可讀 HKLM，**免提權**。標頭註明：鑑識請優先用 OfflineHive。

#### 選用資料庫（需以參數明確提供檔案，無預設路徑）

- **SRUM**（`--srum=SRUDB.dat`）— 經 OS ESE 引擎解析；表含 Network Data Usage、Network Connectivity、Application Resource、Energy Usage、Application Timeline 等（GUID→名稱對照）+ `SruDbIdMapTable` 解析 App/User。每表上限 `100_000`（保留最新、超出警告）。Live 檔 `%SystemRoot%\System32\sru\SRUDB.dat` 鎖定 → 需管理員+VSS 取得；provider 本身只需讀預先複製的檔。
- **BITS**（`--bits=qmgr.db`）— ESE 表 `Files`/`Jobs`。現代 qmgr.db 為單一未公開二進位 blob，採**保守 UTF-16LE 字串擷取**（可列印 run ≥4），分類 URL / drive-UNC 路徑 / 其他；位元組數、狀態、檔案排序**刻意不解碼**。Live 檔 `%ProgramData%\Microsoft\Network\Downloader\qmgr.db`。
- **WMI**（`--wmi=OBJECTS.DATA`）— CIM repository 訂閱持久化三元組（`__EventFilter`/Consumer/`__FilterToConsumerBinding`）。採 triage 法（類 PyWMIPersistenceFinder，可列印 ASCII run ≥3）；**不**做完整 CIM 解析、**不**解碼 consumer 命令列/腳本 payload（需真正的 CIM parser，以免捏造）。Live 檔 `%SystemRoot%\System32\wbem\Repository\OBJECTS.DATA`。

### 跨採集器注意

- **選用 / 無預設路徑**（必須明確提供檔案）：SRUM(`--srum`)、BITS(`--bits`)、WMI(`--wmi`)。
- **完整覆蓋需管理員**：EventLog（Security）、ScheduledTask、PowerShellHistory、StartupFolder、BrowserHistory（其他使用者）、OfflineHive（鎖定 hive 走 VSS / raw 讀取）、ETW。
- **免提權即可運作**：RegistrySearch（預設機碼）、NetConnection、ProcessCrossCheck、LiveService、RecentLnk / JumpList（本使用者）；LiveProcess 可免提權但他人/SYSTEM 程序細節會缺。
- **採集 live SRUM/BITS/WMI 原始檔**本身需管理員 + VSS（檔案被 SYSTEM 鎖定）；Agent 端只負責解析「已取得的檔」，因此實務上多由 KAPE 等先擷取後再以 `--srum/--bits/--wmi` 餵入。

## 部署前檢查清單

在目標主機部署 Agent 或設定排程／回傳前，建議依下列項目確認，以減少執行失敗或回傳遺漏：

| 項目 | 說明 |
|------|------|
| **權限** | 執行帳號具備目標輸出目錄的寫入權限；若需 VSS／離線 Hive，請以**系統管理員**執行。 |
| **磁碟空間** | 輸出目錄所在磁碟有足夠空間（Snapshot 可能數百 MB；長時間採集或含 EVTX 時更大）。 |
| **網路** | 若輸出到網路共用（`\\server\share\...`），確認網路連線穩定、共用可寫入；排程帳號有存取權。 |
| **防毒／EDR** | 必要時將 Agent 路徑或程序加入排除清單，避免採集過程被攔截或檔案被鎖定。 |
| **排程帳號** | 使用 `schtasks` 時，`/ru` 帳號具備上述權限；若為網域帳號，`/rp` 密碼正確或改用「執行時提示」。 |
| **回傳路徑** | `RunCollectAndCopy.ps1` 的 `-CopyToPath` 可寫入；若為共用，目標主機可掛載且權限正確。 |
| **HMAC 金鑰** | 使用 `-StatusHmacKey` 時，金鑰需安全傳遞給接收端，驗證腳本使用相同金鑰。 |
| **設定檔** | 若需自訂 `%AppData%\HostWitness\settings.json`（如 Index.MaxEvents），請在部署時一併放置或於首次執行後設定。 |

檢查完成後再進行「遠端部署流程建議」中的準備與派送。

## 遠端部署流程建議

1. **準備**：在專案目錄執行 `.\publish-agent.ps1`，取得 `Release\Agent\` 資料夾（含 HostWitness.Agent.exe 與相依檔）。
2. **部署**：將整個 `Agent` 資料夾複製至目標主機（或透過遠端派送工具）。
3. **執行**：以具足夠權限之帳號執行，指定輸出目錄（如本機或網路磁碟路徑），例如：  
   `HostWitness.Agent.exe C:\Collect 60 --etw`
4. **回傳**：採集結束後，將輸出目錄（含 `snapshot_*` 子目錄）複製回分析端，以 HostWitness UI 的 **File → Open Snapshot…** 或其它工具檢視 Snapshot。

## 排程、派送與回傳流程

### 排程（Windows Task Scheduler）

以系統或指定帳號定期執行 Agent，可搭配輸出目錄為本機路徑或網路共用。

```powershell
# 建立每日 02:00 執行的排程，採集 120 秒，輸出到 C:\Collect
schtasks /create /tn "HostWitness Agent" /tr "C:\Deploy\Agent\HostWitness.Agent.exe C:\Collect 120 --etw" /sc daily /st 02:00 /ru SYSTEM /f

# 建立每小時執行（採集 60 秒）
schtasks /create /tn "HostWitness Agent Hourly" /tr "C:\Deploy\Agent\HostWitness.Agent.exe C:\Collect 60" /sc hourly /ru SYSTEM /f

# 僅啟用 EventLog + Net，輸出到網路共用
schtasks /create /tn "HostWitness Agent" /tr "C:\Deploy\Agent\HostWitness.Agent.exe \\server\share\Collect 90 --providers=EventLog,Net" /sc daily /st 01:00 /ru DOMAIN\svc_collect /rp PASSWORD /f
```

排程執行後，輸出目錄內會新增 `snapshot_yyyyMMdd_HHmmss`，可依需求保留最近 N 次或依日期清理。

### 派送（部署到目標主機）

- **手動**：複製 `Release\Agent\` 整個資料夾至目標（如 `C:\Deploy\Agent`）。
- **遠端複製**：使用 `xcopy`、`robocopy` 或 PowerShell `Copy-Item` 從派送伺服器複製到目標。
- **群組原則 / MDM**：可透過 GPO 或 MDM 派送資料夾與捷徑，再搭配排程觸發執行。
- **腳本範例（本機部署後執行一次）**：

```powershell
# run-agent-once.ps1：於目標主機執行，採集一次並保留輸出
$agentDir = "C:\Deploy\Agent"
$outDir   = "C:\Collect"
$seconds  = 60
& "$agentDir\HostWitness.Agent.exe" $outDir $seconds --etw
# 結束後 $outDir 內有 snapshot_* 可手動或透過其他機制回傳
```

### 排程與自動回傳腳本

專案內提供 PowerShell 腳本（`scripts\` 目錄）方便排程與自動回傳：

- **ScheduledCollect.ps1**：建立 Windows 排程工作（每日/每小時/每分鐘），可選 `-CopyToPath` 產生回傳腳本；支援 `-RetryCount`、`-RetryDelaySeconds`、`-VerifyCopy`、`-IncludeEvtx`。
- **RunCollectAndCopy.ps1**：執行一次採集後，將最新 `snapshot_*` 複製到指定路徑（如 `\\server\share\Snapshots\HostA`）；支援 copy retry、`manifest.json` hash 驗證與 `-IncludeEvtx`（Application/System/Security）。
- 兩支腳本皆會輸出 `collect-status-latest.json`（位於 OutputDir），含 `success/stage/error`、（`RunCollectAndCopy.ps1` 另含 `agentExitCode`、`exportSkipped`）、snapshot 與目的地路徑、manifest 驗證結果，便於自動化流程判讀。`RunCollectAndCopy.ps1` 結束代碼：`0` 成功；`1` 為 Agent `1`、複製／驗證失敗等；`2` 為 Agent `2`（採集器停止失敗、未匯出 snapshot）。
- 可選 `-StatusHmacKey "<shared-secret>"` 對狀態 JSON 加上 `signatureAlgorithm` 與 `signature`（HMAC-SHA256），供接收端驗證來源完整性。
  - 驗證端可用：`scripts\VerifyCollectStatusSignature.ps1 -StatusPath <collect-status-latest.json> -HmacKey <shared-secret>`
  - 一鍵管線驗證：`scripts\VerifyCollectionPipeline.ps1 -StatusPath <collect-status-latest.json> -SnapshotPath <snapshot_*> -HmacKey <shared-secret>`（輸出 `pipeline-verify-report.json`）

範例（以排程執行採集並自動複製到共用資料夾）：

```powershell
# 建立每日 02:00 採集 120 秒並複製到共用（含 copy 驗證與 EVTX）
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File C:\Deploy\scripts\RunCollectAndCopy.ps1 -AgentPath C:\Deploy\Agent\HostWitness.Agent.exe -OutputDir C:\Collect -CollectSeconds 120 -EnableEtw -CopyToPath \\server\Snapshots\$(hostname) -VerifyCopy -IncludeEvtx"
$trigger = New-ScheduledTaskTrigger -Daily -At 02:00
Register-ScheduledTask -TaskName "HostWitness Collect and Return" -Action $action -Trigger $trigger
```

### 回傳流程與範例指令

1. **手動回傳**：將目標主機上的輸出目錄（例如 `C:\Collect` 或 `\\target\C$\Collect`）複製到分析端本機，例如 `D:\Snapshots\HostA_20260305`。
2. **在 HostWitness UI 開啟**：**File → Open Snapshot…** 選擇該目錄下的 `snapshot_yyyyMMdd_HHmmss` 資料夾（或含該子目錄的父資料夾），即可載入 timeline/entities 並在 Timeline 與各分頁檢視。
3. **指令列 / 腳本回傳範例**（從分析端拉取遠端主機的採集結果）：

```powershell
# 從遠端主機複製最新 snapshot 到本機
$remoteHost = "TARGET_PC"
$remotePath = "C$\Collect"
$localPath  = "D:\Snapshots\$remoteHost"
New-Item -ItemType Directory -Force -Path $localPath | Out-Null
Copy-Item -Path "\\$remoteHost\$remotePath\snapshot_*" -Destination $localPath -Recurse -Force
# 之後在 HostWitness 中 File → Open Snapshot… 選 $localPath 下對應的 snapshot_* 資料夾
```

```cmd
:: 使用 robocopy 同步遠端 Collect 到本機
robocopy \\TARGET_PC\C$\Collect D:\Snapshots\TARGET_PC /E /MIR /R:2 /W:5
```

4. **EVTX 或額外報告**：目前可直接於腳本加 `-IncludeEvtx`，將 Application/System/Security `.evtx` 複製到 `snapshot_*\raw\evtx\` 一併回傳；分析端可依需求以 HostWitness 或其它工具檢視。
5. **完整性驗證**：可於分析端執行 `scripts\VerifySnapshotIntegrity.ps1 -SnapshotPath <snapshot_*>`；或用 `-SnapshotRoot <path> -Recurse` 批次驗證多個 `snapshot_*` 目錄。

### 流程摘要

| 階段 | 說明 |
|------|------|
| 準備 | `publish-agent.ps1` → `Release\Agent\` |
| 派送 | 將 Agent 資料夾複製至目標主機（或透過 GPO/MDM/腳本） |
| 排程 | 使用 `schtasks` 或工作排程器設定執行時間與參數（輸出目錄、秒數、`--etw`/`--providers`） |
| 執行 | Agent 在目標主機執行指定秒數，寫入 `snapshot_*` 至輸出目錄 |
| 回傳 | 手動或腳本將輸出目錄（含 `snapshot_*`）複製回分析端 |
| 分析 | HostWitness UI **File → Open Snapshot…** 開啟對應 snapshot 資料夾 |

## 參考

- 架構：`docs\ARCHITECTURE.md`
- Snapshot 格式：Core SnapshotExporter；限制與風險：`docs\LIMITATIONS.md`

---

**文件更新**：2026-06-19（新增「採集來源一覽」逐 provider 詳列來源路徑/機碼/權限；補正 `--providers=` 名稱清單與 `--evtx/--srum/--bits/--wmi/--repo/--repo-token` 參數；補結束代碼 `3`＝repo 發布失敗）；2026-03-20（補充結束代碼 0/1/2、停止失敗不匯出之行為）；先前版本見歷史條目與根目錄 README。
