# Raw Disk 與離線 Hive 使用說明

## 概述

當 Hive 檔案路徑無法直接存取（例如需從實體磁碟指定區段讀取）時，可使用 **Raw Disk 讀取** 搭配 **OfflineHiveRegistryProvider.AddRawHive**，將指定磁區寫入暫存檔後納入離線分析。

## 前置條件

- **管理員權限**：Raw 讀取 `\\.\PhysicalDriveN` 需提升權限。
- **Offset/Size 來源**：由使用者或外部工具（例如 MFT 解析、磁碟編輯器）提供 Hive 在實體磁碟上的位元組偏移與大小。

## API 使用

```csharp
// 範例：從 PhysicalDrive0 的 offset 0x10000 讀取 512KB 作為 SYSTEM hive
var provider = new OfflineHiveRegistryProvider();
provider.SetSnapshotService(new VssSnapshotService());
provider.AddDefaultHivePaths(); // 可與一般路徑混用

bool added = provider.AddRawHive(
    driveNumber: 0,
    offsetBytes: 0x10000,
    sizeBytes: 512 * 1024,
    hiveName: "SYSTEM"
);
// added == true 表示讀取成功並已加入分析清單；暫存檔於 StopAsync 時會刪除
```

- **driveNumber**：0-based 實體磁碟編號（0 = PhysicalDrive0）。
- **offsetBytes**：Hive 在磁碟上的位元組偏移（建議對齊磁區，例如 512 的倍數）。
- **sizeBytes**：要讀取的位元組數（應涵蓋完整 Hive 檔）。
- **hiveName**：用於識別與查詢的名稱（如 SYSTEM、SOFTWARE），會影響解析時套用的 key 清單。

## 底層讀取

- **RawDiskReader.ReadSectors(driveNumber, sectorOffset, sectorCount)**：以磁區為單位讀取。
- **RawDiskReader.ReadBytes(driveNumber, offsetBytes, sizeBytes)**：以位元組為單位讀取。

失敗時（權限不足、磁碟不存在等）會回傳 null 或空陣列。

## 與 VSS / 一般路徑的關係

- OfflineHiveRegistryProvider 可同時包含：一般 Hive 路徑（含 VSS 解析）與 AddRawHive 加入的暫存檔。
- 離線一致性欄位（OfflineHiveSource、ConsistencyScope、SnapshotTimeUtc）對 Raw 加入的 Hive 仍會依實際來源標註；Raw 讀取產生的暫存檔視為單一時間點資料。

## 參考

- 限制與風險：`docs\LIMITATIONS.md` §13。
- 架構與 Snapshot：`docs\ARCHITECTURE.md`。
