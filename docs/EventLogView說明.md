# Event Log View 說明

## 功能概述
Event Log View 專門顯示由 `EventLogProvider` 產生的事件，提供以 LogName 分類的快速檢視。

## 功能重點
- **LogName 篩選**：Security / System / Application；於本機可用時亦包含 PowerShell、WMI-Activity、Task Scheduler、Windows Defender、Sysmon 等 Operational channel（見下列完整名稱）/ All
- **IR 延伸 channel**（`EventLogProvider` 於 channel 存在且可讀時一併收集；不存在、未啟用或無權限時略過，不影響其他 log）：
  - `Microsoft-Windows-PowerShell/Operational`
  - `Microsoft-Windows-WMI-Activity/Operational`
  - `Microsoft-Windows-TaskScheduler/Operational`
  - `Microsoft-Windows-Windows Defender/Operational`
  - `Microsoft-Windows-Sysmon/Operational`（若不存在再試 `Sysmon/Operational`）
- **Security 稽核對照**：部分事件 ID（如 4698–4701 排程相關）採用保守 Category/Action，避免誤標為服務啟停。
- **關鍵欄位**：Timestamp、LogName、EventId、Level、Source、Summary
- **全文搜尋**：支援 Summary / Source / Level / EventId

## 資料來源
- `WinDFIR.Providers\EventLogProvider.cs`
- 欄位來自 ActivityEvent.Fields：
  - `LogName`
  - `EventId`
  - `Level`
  - `Source`

## 驗證建議
1. 執行 `Release\HostWitness.exe`
2. 切換到 **Event Log** 分頁
3. 透過 LogName 篩選確認 Security / System / Application 事件；若環境具備相應 channel，確認 IR 延伸 channel 事件可出現於清單
4. 點選事件確認細節欄位是否完整

**更新日期：** 2026-04-13（Event Log IR channel 補強）  
**狀態：** ✅ 已完成並編譯
