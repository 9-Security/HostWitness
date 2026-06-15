# HostWitness — 客觀實務評估（2026-06）

> 現況版獨立評估，反映 1.2.0（P1 採集信任儀表板、P2 攻擊鏈/ESE artifact、P4 規模可視化、P6 離線解析 + 交叉比對）完成後的狀態。刻意抽離建置者偏心，含批判。舊版總表見 `ASSESSMENT.md`；逐 artifact 界線見 `LIMITATIONS.md`。

## 規模快照
- 原始碼 ~31k 行、137 個 `.cs`；測試 ~314（50 檔）。
- 22 個 provider、16 個 parser、5 個 analyzer。
- 散布物：單一自含簽章 exe（win-x64，內含 .NET 8）。

## 定位
單機 Windows 的**即時 + 離線 triage / 時間軸關聯**工具。不是完整鑑識套件，而是「到場初判 → 決定要不要升級取證」這一層。

## 客觀優點（站得住的）
1. **artifact 廣度已到標準 triage 線**：live（process / net / service / ETW / event log）+ 離線解析（Prefetch、Amcache、ShimCache、MFT、registry hive、SRUM、BITS、WMI、JumpList、LNK、UserAssist、BAM/DAM、TaskCache）。常見攻擊鏈/持久化線索大致都摸得到。
2. **統一時間軸 + entity 模型**（process / user / file / network key）：跨來源在同一索引關聯——把零散 artifact 串起來，這是「各自跑 EZ tools 出一堆 CSV」流程缺的價值。
3. **可辯護性是真差異化**：採集信任儀表板（RAG）、snapshot manifest 帶 caveats、證據打包進 `raw/`、完整性雜湊、誠實的 `LIMITATIONS.md`。多數 homegrown 工具沒有這層。
4. **交叉比對 tripwire（P6）**：live API vs raw 偵測隱藏（服務 / 排程 / run-key / 進程），多數 triage 工具沒有的思路。
5. **以真實樣本 + ground truth 驗證**：SrumECmd / JLECmd / Get-WinEvent 逐筆對照，而非「能編譯就算數」。
6. **單一自含 exe、免安裝**，IR 落地方便；解析哲學保守（寧缺勿造假證據——BITS/WMI 字串級萃取、FILETIME 年份防呆、WMI 略過 class 定義等）。

## 客觀弱點（要老實講）
1. **驗證廣度其實很窄——最大隱憂。** 幾乎只在**一台機器**（開發者 host + 一份 KAPE 萃取）、**單一 OS build / locale** 驗過。換 Windows 版本、語系、邊界資料，parser 可能就出狀況。「實務上能不能信」取決於跨多樣真實系統的韌性，而這尚未被證明。
2. **個別 parser 成熟度輸給它重造的工具。** SRUM / JumpList / Prefetch / registry 這些，EZ tools（SrumECmd / JLECmd / PECmd / RegistryExplorer）已被社群磨多年。本專案的 parser 較年輕——真正賣點是「統一時間軸 + 信任框架 + 交叉比對」，不是個別 parser 本身。
3. **BITS / WMI 是字串級 triage，非完整解析**（刻意保守）：BITS 無 byte 計數/傳輸時戳；WMI 無 consumer 命令列 payload。深入案子仍須回到 python-cim / bits_parser。
4. **單機而已。** Remote Agent 未產品化（P5），無多機、無中央 console。企業級 IR 本質是多主機。
5. **live tool 的根本盲點**：ring-0 / firmware 級對手騙得過；執行工具本身擾動狀態。P6 交叉比對抬高成本但非保證（兩個 process API 都是 user-mode）。
6. **run-key 交叉比對預設休眠**（需啟用 live registry，forensic 預設關閉）。
7. **缺整類能力**：無記憶體鑑識、無封包擷取、無完整磁碟映像。它是 triage，不是 full forensics。
8. **自簽憑證** → 散布時「不明發行者」摩擦。

## 實務適用對照
| 適合 | 不適合 |
|---|---|
| 單機 Windows 到場初判、scoping（這台要不要升級取證）| 多機企業級事件（Agent 未成熟）|
| 常見惡意軟體 / LOLBin 的持久化獵捕 | 單一 artifact 深挖（用對應專業工具）|
| 需要**帶 caveats、可辯護**的證據交接 | 對抗 kernel / firmware 級對手的唯一依據 |
| 快速跨來源時間軸關聯 + 竄改 tripwire | 記憶體 / 封包 / 完整磁碟鑑識 |
| 教學 / 理解 artifact | 法庭級單一工具（仍需佐證）|

## 與成熟生態的關係
不該定位成 KAPE / Velociraptor / EZ tools / Autopsy / X-Ways 的替代品——那些經多年實戰打磨、覆蓋廣、社群驗證。本專案的合理定位是**它們之上的一層**：快速、可辯護的單機 triage + 關聯 + 竄改 tripwire；個別 artifact 要深究時，仍回到對應專業工具。

## 誠實結論
**工程品質、可辯護性框架、交叉比對思路**明顯高於一般 homegrown 工具，是真價值。但個別 parser 比所重造的成熟工具年輕，且**驗證只在單一主機 / OS**，真實世界韌性未經考驗。

最務實的價值：**一個快速、可辯護的單機 triage + 關聯層**——回答「這台是否被入侵、要不要升級」，而非取代成熟 DFIR 生態。

**下一步最該投資的，不是再加 artifact，而是：**
1. **跨多版本 / 多語系 / 多真實系統的驗證與韌性**（直接決定「實務幫助」這句話成不成立）。
2. **P5 多機化**（若目標是企業場景）。

---
*文件建立：2026-06-15。與 `ASSESSMENT.md`、`LIMITATIONS.md` 對齊；如敘述漂移請更新本表頭日期。*
