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
```

- **輸出目錄**：Snapshot 匯出之路徑；若省略則使用目前目錄下 `AgentOutput`。
- **採集秒數**：採集執行秒數，預設 30；若省略則使用 30。
- **--etw**：啟用 ETW 監控（ETWMonitorProvider）。預設不啟用以減輕負載。
- **--providers=...**：僅啟用所列的採集器（逗號分隔、不區分大小寫）。若省略則啟用除 ETW 外之全部。

**Provider 名稱**：`Process`（LiveProcess）、`Net`（NetConnection）、`EventLog`、`RecentLnk`、`JumpList`、`BrowserHistory`、`Registry`、`OfflineHive`、`ETW`。

### 結束代碼（process exit code）

| 代碼 | 意義 |
|------|------|
| **0** | 採集與停止皆正常，Snapshot 已匯出。 |
| **1** | 採集器**啟動**階段失敗（rollback）；**或** 採集與停止皆正常但 Snapshot **匯出** 失敗。上述情況皆**不會**產生有效本次 Snapshot。錯誤訊息於 stderr。 |
| **2** | 採集**停止**階段有失敗（任一 `StopAsync` 回報錯誤）；**不會**匯出 Snapshot（stderr 會標示 export skipped）。錯誤訊息於 stderr。 |

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

## 採集內容

預設與 UI 版相同之 Providers（不含 StubProvider、不含 ETWMonitor 以減輕負載）：LiveProcess、NetConnection、EventLog、RecentLnk、JumpList、BrowserHistory、RegistrySearch、OfflineHive。使用 `--etw` 或 `--providers=...,ETW` 可加入 ETW 監控。使用 `--providers=...` 可僅啟用指定採集器。設定檔同 `%AppData%\HostWitness\settings.json`（若存在）。

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

**文件更新**：2026-03-20（補充結束代碼 0/1/2、停止失敗不匯出之行為）；先前版本見歷史條目與根目錄 README。
