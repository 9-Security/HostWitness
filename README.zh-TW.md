# HostWitness（中文說明）

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)

**開源的 Windows 單機即時取證與活動關聯工具。** 由 **nine-security Inc.**（作者：Shar）維護。

> 🌏 **English: [README.md](README.md)**

HostWitness 在單一 Windows 主機上採集並關聯即時與離線取證證據——事件記錄、登錄檔 hive、MFT、Prefetch、Jump List、Amcache、SRUM、瀏覽器歷史、執行中程序、網路連線與各種持久化位置——彙整成一條可篩選的統一時間軸。完成的調查可匯出為可攜、帶 SHA-256 完整性雜湊的 snapshot，利於可辯護的事件回應。

## 功能摘要

- **統一時間軸**：關聯即時與離線證據，支援篩選、依時間範圍匯出（CSV/JSON）、時區顯示（Local/UTC）。
- **即時程序與 TCP 檢視**（Procmon / TCPView 風格）：豐富欄位（父程序、Owner SID、Integrity、映像雜湊、Authenticode），可**鑽取**該程序相關的檔案／登錄／網路事件。
- **離線登錄檔解析**（SYSTEM / SOFTWARE / NTUSER / USRCLASS）：含 UserAssist、ShimCache/AppCompatCache、Amcache 與持久化鍵值；保守涵蓋 BITS / WMI / SRUM 子樹。
- **MFT**：可從即時磁區（raw volume → Backup privilege → VSS 後備）或匯出的 `$MFT` 載入；**Prefetch**（版本 10–31，含 Windows 11）；**Jump List / DestList**；**Recent LNK**。
- **事件記錄採集**：除 Security/System/Application 外，於可用時嘗試常見 IR Operational channel（PowerShell、WMI-Activity、Task Scheduler、Defender、Sysmon）。
- **程序記憶體 dump**（minidump / full dump）。
- **Snapshot 匯出**：證據改寫為 bundle-local `raw/` 路徑，附 `hashes.txt` 完整性清單，bundle 可攜且可獨立驗證。
- **鑑識優先預設**（離線 hive、Forensic Strict profile）；Live Registry 為實驗性、需手動開啟。
- **選用的遠端採集 Agent**（`HostWitness.Agent`）——見文件。

## 下載與驗證（SmartScreen 警告處理）

發行版**尚未採用 EV 簽章**，Windows SmartScreen 會顯示「未知發行者」警告——**這是預期行為，不代表檔案有問題**。取證工具會讀取原始磁碟、dump 記憶體、操作特權，比一般程式更容易被 SmartScreen 或防毒標記。

**執行前請務必先用 SHA-256 驗證檔案：**

```powershell
Get-FileHash .\HostWitness.exe -Algorithm SHA256
```

把輸出的 `Hash` 與對應版本 `docs\RELEASE_NOTES_<版本>.md`、GitHub Release 頁公布的雜湊比對。一行自動比對（把 `<EXPECTED_SHA256>` 換成公布值）：

```powershell
if ((Get-FileHash .\HostWitness.exe -Algorithm SHA256).Hash -eq '<EXPECTED_SHA256>') { 'OK：雜湊相符' } else { '警告：雜湊不符，請勿執行' }
```

驗證通過後，於 SmartScreen 畫面點「其他資訊」→「仍要執行」，或在檔案上按右鍵 → 內容 → 勾選「解除封鎖」。完整說明見 **[docs/VERIFY_AND_SMARTSCREEN.md](docs/VERIFY_AND_SMARTSCREEN.md)**（英文）。

## 自行建置

需 **.NET 8 SDK**（簽章另需 Windows SDK 的 `signtool`，可選）。不信任預編譯檔時，最可靠的做法是自行建置——程式碼可稽核、建置可重現。

```bat
git clone https://github.com/9-Security/HostWitness
cd HostWitness
cmd.exe /d /c .\publish.cmd
```

產出 `Release\HostWitness.exe`（self-contained 單檔，win-x64）。其他變體（ARM64、framework-dependent、僅建置測試）與發布細節見 **[docs/建置與發布.md](docs/建置與發布.md)**。

## 系統需求

- Windows 10 / 11，x64（或 ARM64）。
- **raw-disk / MFT、VSS 快照、記憶體 dump、Live Registry** 等功能需**系統管理員權限**；多數唯讀檢視可在一般權限下使用。

## 文件

| 主題 | 文件 |
| --- | --- |
| 使用說明 | [docs/使用說明.md](docs/使用說明.md) |
| 下載驗證與 SmartScreen | [docs/VERIFY_AND_SMARTSCREEN.md](docs/VERIFY_AND_SMARTSCREEN.md) |
| 取證限制與假設 | [docs/LIMITATIONS.md](docs/LIMITATIONS.md) · [docs/FORENSIC_ASSUMPTIONS.md](docs/FORENSIC_ASSUMPTIONS.md) |
| 架構 | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| 開發者說明 | [docs/開發者說明.md](docs/開發者說明.md) |
| 建置與發布 | [docs/建置與發布.md](docs/建置與發布.md) |
| 功能與變更記錄 | [docs/變更摘要.md](docs/變更摘要.md) |
| 遠端採集 Agent | [docs/遠端採集Agent說明.md](docs/遠端採集Agent說明.md) |

## 授權

採用 **Apache License 2.0**——見 [LICENSE](LICENSE)。© 2026 nine-security Inc.

## 免責聲明

HostWitness 為使用者模式（user-mode）即時取證工具，僅可用於你有權調查的系統。執行任何即時工具都會擾動系統狀態，且足夠特權的核心／韌體植入可欺騙使用者模式採集——請將異常視為需佐證的線索，而非證據。見 [docs/LIMITATIONS.md](docs/LIMITATIONS.md)。
