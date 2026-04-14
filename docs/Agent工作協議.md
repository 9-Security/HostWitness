# HostWitness Agent 工作協議

本文件用於降低 agent / coding assistant 在後續修改時的 scope drift，確保變更不因「順手」而跑偏。

適用情境：

- 使用 Codex / agent 進行 review、patch、重構、文件更新。
- 需要多人或多次接力維護同一專案。

---

## 1. 核心原則

1. 先定義邊界，再開始改。
2. 一次只處理一個任務。
3. scope 內做完，scope 外只記錄，不順手修。
4. 每次變更都要有驗收與驗證。
5. 若需求不夠精準，先縮小任務，不要擴大任務。

## 2. 任務預設流程

### 小改動

`review` → `patch` → `verify` → `回報`

### 中大型改動

`review` → `確認 in/out of scope` → `patch` → `verify` → `docs` → `回報`

### 高風險改動

先做 read-only review，不直接 patch：

- 輸出格式
- 採集流程
- 權限與 VSS / raw / backup 路徑
- 刪除 public API / 相容層
- 大量 rename / move / refactor

---

## 3. 每次下任務建議格式

建議直接用下面模板：

```text
任務：
只修這個問題，不做延伸重構。

背景：
這個問題影響……

In scope：
- 只處理……
- 只允許修改……檔案

Out of scope：
- 不改 UI 文案
- 不重構別的模組
- 不升級套件
- 不順手修無關 warning
- 不做額外功能

驗收條件：
- 必須達成……
- 必須保留……

驗證：
- 必跑 `dotnet build ...`
- 必跑 `dotnet test ...`

回報：
- 列出實際修改檔案
- 列出沒做但發現的 scope 外問題
```

---

## 4. Agent 必須遵守

- 不修改未列入 in-scope 的模組。
- 不因為看到 dead code、warning、命名不一致就順手清。
- 不改 public surface，除非任務明確要求。
- 不新增「順便」文件，除非任務要求文件同步。
- 不把 bugfix 擴成重構。
- 不因測試方便而改產品行為。
- 不覆蓋使用者既有未提交變更。

## 5. Agent 應主動回報

- 發現 scope 外問題，但不處理。
- 發現需求互相矛盾。
- 發現現有架構無法在不擴 scope 下安全完成。
- 發現需要額外權限、外部環境或 integration test。

回報格式建議：

- `已完成`
- `未完成 / 原因`
- `scope 外發現`
- `驗證結果`

---

## 6. 如何避免跑偏

### 要做

- 明確指定可改檔案。
- 明確列出 out-of-scope。
- 要求先 `review` 再 `patch`。
- 要求回報實際改動清單。
- 對高風險任務先要 findings，不要直接要實作。

### 不要做

- 不要把「修 bug + 重構 + 補測試 + 補文件 + 順便優化」塞成一個任務。
- 不要只說「幫我整理一下」或「順一下」。
- 不要讓 agent 自己決定是否可改 schema、public API、輸出格式。

---

## 7. 任務分級建議

### Level 1. 單點修補

適合：

- 小 bugfix
- 單檔文件更新
- 明確 dead code cleanup

要求：

- 指定 1 到 3 個檔案
- 直接 patch

### Level 2. 模組內修改

適合：

- 同一功能區塊的小型重構
- 補測試與行為修正

要求：

- 先 review 再 patch
- 指定模組邊界
- 指定必跑驗證

### Level 3. 跨模組或高風險

適合：

- 採集流程
- 輸出格式
- schema 變更
- 權限 / VSS / raw 路徑

要求：

- 先只做 review / plan
- 待確認後才 patch
- 必須同步 docs

---

## 8. 建議固定口令

以下短句很有效：

- `只修這個問題，不做延伸重構。`
- `先 review，等我確認後再 patch。`
- `只允許修改這些檔案：...`
- `如果發現 scope 外問題，只列出，不要順手修。`
- `需要 build/test 驗證，回報實際結果。`
- `若必須擴 scope，先停下來說明原因。`

## 9. 對 HostWitness 特別重要的限制

- `snapshot` / `manifest` / `SQLite schema` 變更要視為高風險任務。
- `Forensic Strict` / `Triage Fast` 行為變更要同步文件。
- VSS / raw / backup / live fallback 順序不可自行更動。
- high-risk 功能如 dump、Live Registry，不得在未要求下擴大行為。
- DFIR 場景中的「摘要、告警、截斷、權限」屬於產品行為，不可當成純 UI 文案處理。

---

## 10. 推薦實際用法

### 範例 A：安全的小修

```text
任務：
移除 confirmed dead code。

In scope：
- WinDFIR.Core/IO/RawDiskReader.cs
- WinDFIR.UI/MainWindow.xaml.cs

Out of scope：
- 不做其他 cleanup
- 不補 unrelated docs

驗收：
- 只移除已確認未使用成員
- build 必須通過
```

### 範例 B：高風險功能

```text
任務：
檢查 snapshot manifest 是否需要加版本欄位。

先做：
- review
- 提 findings

不要先 patch。
```

---

**文件更新：** 2026-03-19（初版：agent anti-drift 工作協議）
