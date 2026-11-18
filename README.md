# TaskMonitor

**繁體中文** ｜ 简体中文、English 介面已在程式內建

以**常駐工作列的覆蓋層或懸浮卡片**，從「裝置 + 行程」兩個層面即時監測系統負載。

<p align="center">
  <img src="assets/header.webp" alt="TaskMonitor">
</p>

本倉庫是 [BloodMoon912/TaskMonitor](https://github.com/BloodMoon912/TaskMonitor)，由原版
[linesoft2/TaskMonitor](https://github.com/linesoft2/TaskMonitor) 分支而來，主要強化了排列方式、
多語言、感測器覆蓋範圍與發佈流程。授權與原作者聲明見文末。

## 下載與安裝

- 下載位置：[GitHub Releases](https://github.com/BloodMoon912/TaskMonitor/releases)
- 單一 exe，解壓即用；不需要安裝 .NET、不需要額外套件。
- 每個釋出檔旁邊有 `.sha256` 校驗檔，可自行比對：

```powershell
Get-FileHash .\task_monitor-2027.11.18.BM-x64.exe -Algorithm SHA256
```

- 首次啟動會：① 詢問是否同意 UAC 提權（程式需要它才能讀 SRUM / 磁碟效能計數器）→ ② 顯示啟動指南，
  說明覆蓋層會停在哪一側。之後每次啟動都直接提權執行。

### 關於數位簽章

發布流程支援 Authenticode 簽名：CI 上只要有 `SIGN_PFX_BASE64` + `SIGN_PFX_PASSWORD` 這兩個 secret，
釋出檔就會被簽名，並在 Release 頁的摘要標明「已簽名」。**簽名憑證沒有設定時，產物就是未簽名版本**，
Windows SmartScreen 會顯示「不明的發行者」——這是預期行為，不是檔案損壞。

驗證簽章：

```powershell
Get-AuthenticodeSignature .\task_monitor-2027.11.18.BM-x64.exe | Format-List Status, SignerCertificate
```

> 本機想先試簽名流程，可跑 `tools/new-dev-cert.ps1` 產生自簽憑證——它只在你自己電腦上有效，
> 換到別台機器一樣會被當成不明發行者。

## 使用

下載開啟後，**工作列**上會出現常駐的監控覆蓋層（位置取決於工作列設定，可貼左／貼右／垂直任務列）：

<p align="center">
  <img src="assets/taskbar_window.png" alt="TaskMonitor">
</p>

- **左鍵點一個圖磚** → 開啟該項目的詳情視窗（趨勢圖 + 高佔用行程）。
- **右鍵** → 懸浮模式、置頂顯示、貼邊隱藏、全螢幕時隱藏、緊湊模式、字體大小、語言、設定、結束。
- **拖動** 覆蓋層可換位置；設定 → 外觀 可切換 懸浮模式（不嵌入任務列的自由卡片）。

## 功能

- **CPU**：使用率、目前頻率、行程數、執行緒數、控制代碼數、執行時間、封裝功耗、核心電壓、風扇轉速、溫度。
- **記憶體**：使用中、可用、已提交、已快取、分頁集區、非分頁集區，以及各行程佔用。
- **磁碟**：依實體磁碟分開採集——名稱、類型、使用率、讀寫速度、平均回應時間、溫度、已用／總容量，
  以及各行程讀寫速度。
- **GPU**：多 GPU 分別採集——使用率、溫度、功耗、核心頻率、風扇轉速、負載類型、專用／共用記憶體，
  以及各行程的 GPU 使用率。
- **網路**：依網卡分開採集——即時速率、網路類型、SSID、Wi-Fi 標準、協商速率、本機 IPv4/IPv6、
  公網 IP、本地與公網延遲，以及各行程即時網速；可與 Clash / Mihomo 整合顯示代理後速率。
- **監控歷史**：每分鐘聚合成一筆（平均／最高／最低），可查 1 小時 / 6 小時 / 24 小時 / 7 天，
  明細寫入 `history\yyyy-MM-dd.csv`，保留 30 天。
- **其他**：深色／淺色主題、三種語言（简中 / 繁中 / English）、四段字體大小、取樣間隔、
  合併相同程式、結束行程。

## 介面設計

- 覆蓋層每一格是一個**圖磚**：同一裝置的「使用率 + 溫度」共用一個標題，上下兩列數值，
  整欄只有一個可按區，hover 整欄高亮。
- **欄寬由量測文字決定**（標籤寬 + 最寬可能值），不是手寫常數——數字從 9% 變 10% 不會讓覆蓋層抖動。
- 項目順序在 設定 → 外觀 → 項目順序：六張卡片（CPU、GPU、RAM、磁碟、VRAM、網路），
  一張一個圖磚，拖動一次帶兩列，開關決定它要不要出現。
- 分割線預設關閉；圖磚與單列圖磚內部不會被橫線切開。

## 系統需求與限制

- Windows 10 / 11，x64。只依賴系統內建的 .NET Framework 4.8。
- 程式**一律以管理員權限執行**（行程網路資料來自 SRUM，需要提權）。
- CPU 溫度／風扇來自 LibreHardwareMonitor 感測包（首次使用時按需載入，經 PawnIO 讀取）；
  防毒軟體偶爾會對這類底層存取有意見——感測包沒載入時，溫度顯示 `--`，其餘功能不受影響。
- 部分資料來自未文件化的 API，Windows 更新後可能需要調整。
- 公網 IP / 延遲會向第三方服務發請求，可在設定關閉（關閉後不再有任何對外請求）。
- 未設定簽名憑證的釋出檔沒有數位簽章。

## 專案結構

```
src/
  App.xaml(.cs)         進入點：提權閘門、單實例 mutex、右鍵選單、設定寫回
  AppSettings.cs        settings.yaml 的模型
  Logger.cs             日誌（logs/task_monitor-<date>.log，保留 7 天）
  VersionInfo.cs        對外版本字串（讀 AssemblyInformationalVersion）
  Localization/         Loc（id → 字串）、lang/*.yaml 載入、XAML 擴充與視覺樹翻譯
  Sampling/             SystemSampler 與各 Sampler；Sensors/ 是按需載入的感測包
  History/              每分鐘聚合與 CSV 落地
  Interop/              P/Invoke 與崩潰追蹤
  UI/                   TaskbarWindow（覆蓋層）、DetailWindow、Details/、Charts/、
                        Settings/、History/、Common/
lang/                   zh-CN.yaml、zh-TW.yaml、en.yaml（三檔 key 集合必須一致）
docs/                   gotchas / architecture / settings-plumbing / scroll-stack
tools/                  pack.ps1（打包）、sign.ps1（簽名）、new-dev-cert.ps1、
                        check-loc-coverage.ps1、publish-cnb-release.ps1
assets/                 logo 與 README 圖片
.github/workflows/      release.yml（tag → 建置 → 打包 → 簽名 → Release）、sync-cnb.yml
```

## 建置與開發

只有 `dotnet`（.NET SDK）就夠，不需要 Visual Studio 的 MSBuild：

```powershell
dotnet build -c Debug
bin\Debug\net48\task_monitor.exe
```

程式執行中會鎖住輸出檔，`dotnet build` 會在複製步驟失敗（`MSB3021`/`MSB3027`）——那不是編譯錯誤。
先請程式自己結束：

```powershell
New-Item bin\Debug\net48\shutdown.sentinel -ItemType File -Force   # 1 秒內輪詢到就優雅退出
```

### 打包

```powershell
pwsh -File tools\pack.ps1                 # 建置 Release + dist\task_monitor-<version>-x64.exe + .sha256
pwsh -File tools\pack.ps1 -Zip            # 再加一個 zip
pwsh -File tools\pack.ps1 -Sign           # 打包後簽名（沒憑證就失敗，避免漏簽）
```

版本字串讀 `<InformationalVersion>`，退回 `<Version>`。產物一律落在 `dist/`（不進版控）。

### 簽名

```powershell
pwsh -File tools\sign.ps1 -File dist\task_monitor-2027.11.18.BM-x64.exe -PfxPath certs\dev.pfx
pwsh -File tools\new-dev-cert.ps1         # 本機測試用的自簽憑證（只在你自己這台有效）
```

憑證來源優先序：`-PfxPath` → `-PfxBase64` → 環境變數 `SIGN_PFX_BASE64` → `SIGN_PFX` +
`SIGN_PFX_PASSWORD` → `-Thumbprint` → `SIGN_CERT_THUMBPRINT`。簽完會自動 `signtool verify /pa`。
時間戳使用 DigiCert；長期可驗證性取決於時間戳是否成功。

## 發版流程

1. 改 `task_monitor.csproj`：`<Version>`（四段數字，例如 `2027.11.18`）與
   `<InformationalVersion>`（對外字串，例如 `2027.11.18.BM`）。
2. 在 `CHANGELOG.md` 最上方寫 `## v<InformationalVersion>` 段落（Release 文案就是從這裡抓的）。
3. `git tag v2027.11.18.BM && git push origin v2027.11.18.BM`。
4. `release.yml` 自動：驗 tag 與版本一致 → Release 建置 → `tools/pack.ps1` 產出 exe + sha256 →
   （有 `SIGN_PFX_BASE64` 才）簽名 → 建 GitHub Release 並附上檔案。
5. CNB 鏡像發布是可選步驟：沒有 `CNB_TOKEN` / `CNB_REPO` 就整段跳過。

## 參考 / 致謝

- [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)（工作列監控的經典實作）
- [LiteMonitor](https://github.com/Diorser/LiteMonitor)（本分支的欄寬量測與圖磚排列參考了它的做法）
- Windows 工作管理員（取樣方式與計算邏輯對齊）
- [iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern)（Fluent 2 控制項）
- [DirectN](https://github.com/smourier/DirectN)（Direct2D / DirectComposition）
- [FluentWpfCore](https://github.com/TwilightLemon/FluentWpfCore)（亞克力材質）
- [YamlDotNet](https://github.com/aaubry/YamlDotNet)（`lang/*.yaml` 解析）
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（MPL-2.0，感測包）
- [PawnIO](https://pawnio.eu/)（感測包的底層存取）

## 授權與版權

原版 Copyright © 2026 双霖 (LineSoft)。本分支的修改 Copyright © 2027 BloodMoon912。

依 [Apache License 2.0](LICENSE) 授權使用；依該授權要求，原作者的著作權聲明與 `LICENSE` 一併保留。