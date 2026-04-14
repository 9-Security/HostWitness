# UI 布局驗證說明

## 📋 預期布局

目前 UI 採用「上下雙 Tab + 共用內容區」：

```
┌─────────────────────────────────────────────────────┐
│ Menu Bar (File / Advanced / Help)                   │
├─────────────────────────────────────────────────────┤
│ Dynamic Tabs: Timeline | Live Stream | Live Process | Live TCP View
├─────────────────────────────────────────────────────┤
│ Static Tabs:  System Info | Process View | Recent Files | Prefetch | Amcache | Autorun |
│              Event Log | Netstat | Browsing History
├─────────────────────────────────────────────────────┤
│ Shared Content Area (顯示目前選中的視圖)             │
└─────────────────────────────────────────────────────┘
```

## 🔍 驗證步驟

### 1. 確認運行新版本

**重要**：請確保運行的是最新編譯的版本！

```powershell
# 方法一：直接運行
cd "d:\cursor\IR_script\Live Response"
.\Release\HostWitness.exe

# 方法二：使用啟動腳本
.\啟動HostWitness.bat
```

**檢查可執行檔時間**：
- 請以建置輸出或 `Release\HostWitness.exe` 的檔案修改時間為準。
- 位置：`Release\HostWitness.exe`（執行 **`publish.cmd`** 產生）。

### 2. 檢查布局結構

啟動後，請確認：

**動態分頁（Dynamic）：**
- ✅ 應該看到 4 個標籤：**Timeline View**、**Live Stream**、**Live Process**、**Live TCP View**
- ✅ 這些標籤應該在**上方第一排**

**靜態分頁（Static）：**
- ✅ 應該看到 **System Info / Process View / Recent Files / Prefetch / Amcache / Autorun / Event Log / Netstat / Browsing History**
- ✅ 這些標籤在**第二排**

**共用內容區：**
- ✅ 切換任何分頁，內容區會切換到對應視圖

**狀態列（PID 快取）：**
- ✅ 內容區下方顯示 PID 快取狀態（ETW / EventLog）
- ✅ 可在 Advanced → Settings 啟用/停用狀態列診斷（PID cache / ETW throttle / UI queue）；設定需重啟套用
- ✅ 可透過 `%AppData%\HostWitness\settings.json` 調整 TTL/LRU/長駐清單
- ✅ 顯示 ETW 節流狀態（last drops）

**啟動預設分頁：**
- ✅ 程式啟動時預設選中 **System Info**（靜態分頁），動態分頁不預選；內容區顯示 System Info 視圖。

**動態分頁拆出：**
- ✅ 動態分頁工具列有 Detach / Restore 按鈕（僅圖示，ToolTip 顯示「Detach」或「Restore」）
- ✅ Detach 後會開新視窗，主視窗顯示「已拆出」提示
- ✅ 拆出的新視窗工具列與主視窗一致：Play/Pause、Clear、Refresh、Restore、Resolve、States、TCP/UDP 篩選皆為圖示按鈕（無文字），與主視窗圖示統一

**Settings 視窗：**
- ✅ Advanced → Settings 會開啟設定視窗
- ✅ 可直接在視窗內調整 PID 快取參數（EventLog/ETW TTL、MaxEntries、長駐程序清單）
- ✅ 可在視窗內選擇全介面字型大小（小/中/大/自訂）
- ✅ **Activity Index (Timeline)**：Max events in memory（0 = 無上限）

**Live Process 右鍵選單：**
- ✅ 清單與 **Show process tree** 樹狀檢視下皆可右鍵：Apply as Filter、Copy Value、Open Directory、**Create Dump > Create minidump / Create full dump**、**Show related events (Drill down)**
- ✅ dump 其他程序需以管理員執行 HostWitness

**Drill-down（關聯事件跳轉）：**
- ✅ 在 Live Process 選中一行程式後，雙擊該列或右鍵「Show related events (Drill down)」會切換至 **Timeline View** 並僅顯示該 Process 的 File/Registry/Network 等事件
- ✅ Advanced → Drill Down 選單在 Live Process 分頁時啟用；點擊後若已選中程式則同上跳轉
- ✅ Timeline 顯示「Filtered by PID: xxx」與「Clear process filter」按鈕可還原全量時間軸

**Autorun（Registry Run/RunOnce）：**
- ✅ 靜態分頁 **Autorun** 顯示開機／登入 Run 相關機碼（Run、RunOnce、User Run、User RunOnce、RunServices、RunServicesOnce、PoliciesRun、StartupApprovedRun、Winlogon、IFEO），對齊 Sysinternals Autoruns 的 Registry 檢視
- ✅ 可依 Location 下拉篩選與搜尋；欄位：Location、Entry、Command、Last Write、Hive；下方 Entry Details 顯示完整機碼路徑與型別
- ✅ **Entry Details 選取在 Refresh 後保留**（SelectedEntry 綁定與還原），不會因週期 Refresh 幾秒後自動清空
- ✅ 資料來自 Offline Hive（需先啟動資料收集，OfflineHiveRegistryProvider 會解析 SOFTWARE/NTUSER）

### 3. 如果布局不對

如果看到的布局與預期不符，請提供：

1. **具體描述**：
   - 標籤的位置（都在同一行？還是分開？）
   - 視圖內容的位置
   - 是否有分隔線

2. **截圖**：
   - 完整的應用程式截圖
   - 標註出與預期的差異

3. **可執行檔資訊**：
   - 運行的可執行檔路徑
   - 可執行檔的修改時間

---

---

**更新日期**：2026-02-02（依目前狀態全文件同步）
**Process Tree：**
- ✅ Live Process 勾選 Show Process Tree 後，應看到樹狀層級（父/子程序）
- ✅ 若只看到扁平清單，請確認 Parent PID 是否有填入（需重新啟動程式讓新事件產生）
