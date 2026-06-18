# Case Repository 與證據彙整 (Intake) 部署說明

P5 多機化：把多台受查主機的 snapshot bundle 集中到一個 case repository。共有兩種傳輸，都實作同一個 `IArtifactSink` 契約（完整性把關、冪等、可續傳）：

- **FileSystem sink** — 共享資料夾 / UNC / 掛載 bucket。零基礎設施。
- **HTTP intake** — 受查主機無法觸及共享路徑、但能連到彙整伺服器時使用。

repository 版面：`<repo>/<hostname>/<collectionId>/`。`collectionId` 是每次採集的 GUID（寫在 `manifest.json`），是冪等鍵——重複發佈同一採集為 no-op。

---

## 1. FileSystem repository（最簡單）

Agent 採集後直接發佈到共享路徑：

```
HostWitness.Agent.exe C:\Temp\out 60 --repo=\\fileserver\cases\IR-2026-0616
```

或在桌面 UI：**File → Publish to Case Repository…**，repository target 填共享資料夾路徑。

只要受查主機對該路徑有寫入權限即可。不同主機的 bundle 因 hostname + collectionId 分流，不會互相覆蓋。

---

## 2. HTTP intake 伺服器

當受查主機只能走 HTTP 時，在彙整主機上跑一個 intake server，包住 `BundleIntakeService`：

```csharp
var service = new BundleIntakeService(@"D:\cases\IR-2026-0616");
using var server = new HttpListenerBundleIntakeServer(
    service,
    urlPrefix: "https://intake.example.local:8443/",
    authToken: "<長隨機字串>");
server.Start();
Console.WriteLine("Intake listening. Ctrl+C to stop.");
Thread.Sleep(Timeout.Infinite);
```

受查主機端：

```
HostWitness.Agent.exe C:\Temp\out 60 --repo=https://intake.example.local:8443/ --repo-token=<同一字串>
```

UI 端：repository target 填 `https://…`，token 填到「Intake token」欄。

### 端點（`HttpIntakeContract`）

| 方法 | 路徑 | 用途 |
|---|---|---|
| GET | `bundles/{collectionId}/status` | 是否已完成？哪些檔案已暫存（續傳用） |
| PUT | `bundles/{collectionId}/files?path=<rel>` | 上傳單檔（標頭 `X-Content-Sha256`） |
| POST | `bundles/{collectionId}/complete` | 完整性把關後 finalize 進 repository |

---

## 3. URL ACL（非系統管理員執行時必要）

`HttpListener` 綁定非 localhost 的 prefix 需要 URL 保留。以系統管理員執行一次：

```
netsh http add urlacl url=https://intake.example.local:8443/ user="DOMAIN\intakeSvc"
```

（測試用 localhost prefix 在多數環境不需此步；本專案的 socket 測試若遇 `HttpListenerException` 會自動略過。）

---

## 4. TLS（強烈建議：token 是憑證，明文傳輸會外洩）

`HttpListener` 的 https 是把憑證綁到「IP:port」，不在程式碼裡設定。以系統管理員：

```
:: 用憑證的指紋與一個固定 appid（任意 GUID）綁定
netsh http add sslcert ipport=0.0.0.0:8443 certhash=<憑證指紋40字元> appid={00112233-4455-6677-8899-aabbccddeeff}
```

- 憑證可用內部 CA 簽發，或自簽後讓受查主機信任。
- 綁定完成後，伺服器用 `https://…:8443/` prefix 即可（程式碼不變，client 也自動走 TLS）。
- **沒有 TLS 時**：`--repo-token` 會以明文 `Authorization: Bearer` 傳送。僅可用於完全信任的內網，或在反向代理（nginx/IIS ARR）上終結 TLS 再轉發到 `HttpListener`。

### 認證行為

- `authToken` 為 null/空 → 認證關閉（信任內網模式，所有請求放行）。
- 設了 token → 每個請求都檢查 `Authorization: Bearer <token>`，常數時間比對；不符回 `401`。
- client 收到 `401` 會回 `Failed`（不是丟例外），訊息提示檢查 token。

---

## 5. 並發與限制

- 不同 `collectionId`（不同採集）永遠安全並行——GUID 不撞。
- 同一 `collectionId` 在**同一行程內**的重疊發佈，由 process-wide keyed lock 序列化（`KeyedAsyncLock`），不會破壞 `.partial` 暫存或 finalize。
- 同一 `collectionId` 由**兩個不同行程**同時寫到共享檔案系統，目前不互鎖（實務上幾乎不會發生，因 collectionId 是每次採集唯一）。若有此需求，需在 finalize 加跨行程鎖檔。
- intake 目前無上傳大小上限/速率限制——僅供信任內網。對外暴露請在反向代理層加限制與 TLS。
