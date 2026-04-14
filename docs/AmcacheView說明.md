# Amcache View 說明

## 功能概述
Amcache View 解析 `Amcache.hve`，用於列出已安裝或執行過的程式與相關檔案紀錄。

## 顯示欄位
- Type（Application / File）
- Name / Publisher / Version / Product Name
- Path / SHA1 / File Id / File Size
- Last Write (UTC) / Install Date (Raw)

## 資料來源
- `WinDFIR.Providers\Parsers\AmcacheParser.cs`
- Hive 來源：`%SystemRoot%\AppCompat\Programs\Amcache.hve`（若可用會使用 VSS 快照）

## 驗證建議
1. 執行 `Release\HostWitness.exe`
2. 切換到 **Amcache** 分頁
3. 確認列表能顯示應用程式或檔案紀錄

**更新日期：** 2026-02-02（依目前狀態全文件同步）  
**狀態：** ✅ 已完成
