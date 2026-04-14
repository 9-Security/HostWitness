# Prefetch View 說明

## 功能概述
Prefetch View 提供與 WinPrefetchView 類似的檢視方式：
- 上半部：Prefetch 主表格（執行記錄、Run Count、Last Run Time）
- 下半部：Referenced Files 清單（Full Path）
 - 選取保持：刷新後保留原先選取的 Prefetch 記錄

## 顯示欄位
上半部主表格包含：
- Filename
- Created Time
- Modified Time
- File Size
- Process EXE
- Process Path
- Run Count
- Last Run Time

下半部清單包含：
- Filename
- Full Path

## 資料來源
- `WinDFIR.Providers\Parsers\PrefetchParser.cs`
- 解析 `%SystemRoot%\Prefetch\*.pf`，支援版本：10/13/15/17/23/26/30/31（含 Windows 11）

## 為何 Prefetch 是空的？
清單為空時，畫面上會顯示原因提示，常見情況如下：
1. **權限不足**：讀取 `C:\Windows\Prefetch` 需要**系統管理員權限**。請以「以系統管理員身分執行」啟動 HostWitness，再切到 Prefetch 分頁並按重新整理。
2. **資料夾不存在**：若系統碟或 Windows 目錄不在預設路徑，會提示「Prefetch 資料夾不存在」。
3. **無 .pf 或已停用**：若資料夾內沒有可解析的 .pf 檔案，或系統已停用 Prefetch，會提示「資料夾內沒有可解析的 .pf 檔案，或 Prefetch 已停用」。

## 驗證建議
1. 執行 `Release\HostWitness.exe`
2. 切換到 **Prefetch** 分頁
3. 點選任意 Prefetch 記錄，下半部應顯示相關參考檔案清單

**更新日期：** 2026-03-05（支援 v31、空清單原因說明）  
**狀態：** ✅ 已完成
