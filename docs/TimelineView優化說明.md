# Timeline View 優化說明 (LastActivityView 整合)

## 🆕 功能更新（整合 LastActivityView 精神）

### 1. 介面重新設計
我們將 **Timeline View** 改造為類似 `LastActivityView` 的綜合歷史分析工具，而不僅僅是即時監控：
- **欄位標準化**：調整欄位以匹配鑑識需求：
  - `Action Time`: 事件發生的精確時間。
  - `Action`: 動作類型（如 Run, Open, Connect）。
  - `Description`: 詳細描述。
  - `Process/Source`: 觸發動作的執行檔或來源。
  - `File Path`: 相關檔案路徑。
  - `URL`: 相關網址。
  - `Data Source`: 證據來源類型（Browser, Prefetch, EventLog 等）。

### 2. 操作優化
- **選取保持**：現在切換過濾條件或自動刷新時，系統會智慧保留您當前選中的事件，不會再因為畫面跳動而丟失焦點。
- **可調整高度**：如同 Browsing History，Timeline View 下方的詳細資訊面板現在也可自由調整高度 (Splitter)。
- **資料排序**：預設由新到舊排序 (Descending)，方便查看最近的使用者活動。

### 3. 資料聚合
此視圖現在聚合了：
- **Live Events**: Process, Network
- **Historical Artifacts**: Browser History, Event Logs, Registry (Live + Offline), RecentDocs/LNK/JumpList

### 4. Drill-down 依 Process 篩選（2026-02-02）
- 從 **Live Process** 選中程式後雙擊或右鍵「Show related events (Drill down)」會切換至 Timeline 並僅顯示該 Process 的 File/Registry/Network 等事件。
- Timeline 顯示「Filtered by PID: xxx」與「Clear process filter」按鈕可還原全量時間軸。

## 🔍 驗證建議
1.  **啟動程式**：執行 `Release\HostWitness.exe`。
2.  **檢視 Timeline**：確認是否混和了瀏覽紀錄 (Browser) 與其他活動。
3.  **檢查欄位**：確認 `File Path` 和 `URL` 是否有正確對應到相關事件。
4.  **測試選取**：點選一筆資料，等待幾秒（模擬自動刷新），確認游標不會跑掉。

---

**更新日期：** 2026-02-02（依目前狀態全文件同步；時區顯示見 Settings TimeZoneDisplay）
**狀態：** ✅ 已完成並編譯
