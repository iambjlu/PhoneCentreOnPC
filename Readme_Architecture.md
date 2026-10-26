# PhoneCentreOnPC 架構說明

PhoneCentre 的 Windows 版（WinUI 3，C# / .NET 10 / Windows App SDK 1.8）。透過 adb 連線手機，用 scrcpy-server 做畫面鏡像與遠端操作；
功能與外觀對照 Mac 版（`../PhoneCentre`）。手機端（scrcpy-server、`phonecentre-helper.jar`）與 Mac 版共用，原封不動。

- 目標：使用者雙擊就能用的**單一 exe**（約 94 MB，內含 .NET 執行階段、Windows App SDK、精簡 FFmpeg、scrcpy-server、手機端輔助程式），免安裝。
- 平台：Windows 10 1809 以上；x64（Intel／AMD）為主，ARM64 順便支援。
- 還沒移植的功能：檔案、通知、簡訊、通話記錄、相機膠卷、設定視窗、遠端 PhoneCentre（分享）、桌面模式、獨立 App 視窗、模擬定位、聲音、畫質設定、輸入法、解鎖密碼、系統匣、無線裝置自動重連。介面上這些按鈕與分頁已經在，點了會顯示「尚未移植」。

## 目錄

```
PhoneCentreOnPC.csproj      專案（unpackaged、self-contained、PublishSingleFile；x64／ARM64）
App.xaml(.cs)               程式進入點、全域樣式（NavButtonStyle）、--mirror 測試參數
MainWindow.cs               主視窗
MirrorWindow.cs             鏡像視窗
Adb/                        adb 協定與裝置
Mirror/                     scrcpy 工作階段、控制協定、H.264 解碼
Services/                   裝置管理、手機狀態、App 清單
Pages/                      總覽、找不到 adb 的設定頁
Ui/                         Material 圖示、狀態環、三星導覽圖示、共用零件
Support/                    Shell、資源解出、記錄、拖放、視窗管理
Settings/                   設定（settings.json）
Native/{x64,arm64}/         精簡 FFmpeg DLL（編進 exe）
ThirdParty/                 scrcpy-server、授權聲明
Resources/                  phonecentre-helper.jar（手機端輔助程式，已建置）
AndroidHelper/              手機端輔助程式原始碼（Java，與 Mac 版相同）
scripts/                    部署、截圖、FFmpeg 編譯腳本
```

## 建置與打包

- 發行：`dotnet publish PhoneCentreOnPC.csproj -c Release -r win-x64 -p:Platform=x64`（ARM64 同理）。`WindowsPackageType=None`＋`WindowsAppSDKSelfContained`＋`SelfContained`＋`PublishSingleFile`＋`IncludeNativeLibrariesForSelfExtract`；Windows App SDK 的單檔發行需要 `EnableMsixTooling=true`。
- 內嵌資源：`scrcpy-server`、`phonecentre-helper.jar`、依平台的 FFmpeg DLL 都是 `EmbeddedResource`（`LogicalName` 為檔名）。`Assets.Extract` 第一次使用時解到 `%LOCALAPPDATA%\PhoneCentre\assets`（大小相同就沿用），因為 `adb push` 與 `LoadLibrary` 都需要實體檔案。
- 日常流程（Mac 上寫、ARM 機台編譯、Intel 機台驗證）見 `CLAUDE.md`，腳本：
  - `scripts/deploy.sh`：打包原始碼送到 ARM 機台，用排程工作執行 `dotnet publish`（SSH 長時間占線容易斷，所以用排程工作加完成旗標檔輪詢）。
  - `scripts/shot.sh` ＋ `scripts/windows/shot.ps1`：在機台的桌面會話（互動式排程 `/it`）啟動 exe、截圖、收集 `log.txt`。參數寫進小腳本再執行，避開 `schtasks /tr` 的多層引號。
  - `scripts/build-ffmpeg.sh`：在 Mac 上用 llvm-mingw 交叉編譯精簡 FFmpeg。
- 防火牆：adb.exe 第一次執行 Windows 會詢問是否允許存取網路；沒回應會被自動建立阻擋規則，之後每次都再問（阻擋優先於允許）。使用者第一次執行時也會遇到，之後要在介面上先提醒。

## 視窗外觀

- 視窗用 Mica（主視窗）與 Desktop Acrylic（鏡像視窗），不做 Liquid Glass；Mac 版的 `liquidPanel` 對應 `Ui.Panel`（半透明灰底＋細邊框＋圓角）。全部深色主題（`App.xaml` 的 `RequestedTheme="Dark"`）。
- 圖示：`Ui/MaterialIconData.cs` 是 Material Icons（Round，Apache-2.0）的 SVG 路徑，`MIcon` 用 `Path`＋`Viewbox` 畫，顏色跟著 `Foreground`；不用字型檔（單一 exe 裡 `ms-appx` 字型路徑不可靠）。要加圖示時從 `@material-design-icons/svg` 的 `round/<名稱>.svg` 取 `d` 路徑加進去。
- `Ui.Text(…)` 不能把 `Foreground` 設成 null（WinUI 裡 null 畫刷＝不上色，文字會隱形）。
- `.ps1` 腳本要存 UTF-8 with BOM。

## Adb/

### AdbSocket.cs
對應 `AdbSocket.swift`：`AdbEndpoint`（`Local` 不能用 `new()`——record struct 的 `new()` 是全零 default，不套用預設參數）、`AdbException`、阻塞式 socket（`Socket`＋收發逾時；逾時與錯誤都視為連線已關閉）、adb server 協定的 `SendRequest`／`HostQuery`／`Open`（`host:transport:<serial>` 後開服務）。主機若是 IP 字面值就不走 DNS。

### AdbClient.cs
短指令走 `exec:`（`ExecAsync`／`ShellAsync`），push／pull／install／pair／mdns 走 adb.exe（遠端端點加 `-H -P`），`ConnectAsync`／`DisconnectAsync` 走 `host:connect:`。

### Device.cs
`Device`（serial、狀態、型號、端點；`Id`、`Key`＝快取與設定用的裝置鍵：優先 ro.serialno）與 `DeviceInfo`（`FetchAsync` 一次抓取，每個欄位加名稱再解析並丟掉錯誤訊息，關鍵欄位不合理就回傳 null）。

### AdbLocator.cs
找 adb.exe：自訂路徑 → `%LOCALAPPDATA%\PhoneCentre\platform-tools` → ANDROID_HOME／ANDROID_SDK_ROOT → Android SDK → PATH，每個候選先試跑 `adb version`（結果快取）。找不到時 `InstallAsync` 從 Google 下載 Windows 版 platform-tools 並解壓。adb 不隨 App 散布（Android SDK 授權條款）。

### HelperTool.cs
推送（大小不同時才推）並執行手機端輔助程式（`list`、`icons`、`thumbs`、`removetasks` 等），參數做 shell 跳脫。

## Mirror/

### ControlMessage.cs
scrcpy 3.3.4 控制協定編碼（big-endian），與 Mac 版 Swift 逐位元組比對過（16 種訊息完全一致）。

### ScrcpySession.cs
對應 `ScrcpySession.swift`：推送 server、以 `shell:` 啟動（app_process 參數壓在 240 字元內：三星 S10 的 libstagefright 會把 cmdline 讀進固定緩衝區，超過約 257 字元就崩潰）、依序連 video → audio → control、讀影像／音訊封包迴圈、剪貼簿與控制佇列。解碼不在這裡：`VideoConfig`／`VideoFrame`／`AudioPacket` 在背景執行緒呼叫，`StateChanged`／`FrameSizeChanged`／`DeviceClipboard` 回到建立者的 `SynchronizationContext`（UI 執行緒）。

### H264Decoder.cs ＋ FFmpegNative.cs
用 FFmpeg（libavcodec）軟體解碼 H.264，經 swscale 轉 BGRA，與 scrcpy 一致。關鍵幀前補 SPS/PPS，必須從關鍵幀開始；`low_delay`＋slice 執行緒（frame 執行緒會多出幾幀延遲）。雙緩衝，UI 每個畫面刷新用 `TryTakeFrame` 取最新一幀（不排隊）。
- 不用 Windows 內建的 Media Foundation（`MediaStreamSource`）：同一份程式在 ARM64 有輸出，x64（Intel 真機與 ARM 機台模擬）卻吞了封包一幀都不吐，也沒有錯誤訊息可查。
- `FFmpegNative` 只用函式 P/Invoke，不用 AutoGen；`AVFrame`／`AVPacket` 只讀開頭那段穩定 ABI 的欄位（frame：data@0、linesize@64、width@104、height@108、format@116；packet：data@24）。DLL 依 avutil → swscale → avcodec 順序載入。
- `Native/`：精簡 FFmpeg 7.1.1（只含 h264 解碼器與 parser、swscale，LGPL 共享庫；x64 約 4 MB、ARM64 約 2.8 MB，只依賴系統的 UCRT），由 `scripts/build-ffmpeg.sh` 交叉編譯（x64 需要 nasm 才有 SIMD）。`--enable-parser=hevc` 是為了讓 `aom_film_grain.o` 被編進去，否則只開 h264 時 `h2645_sei.o` 缺符號、連結失敗。

### MirrorConfig.cs
畫質選項與鏡像參數，預設同 Mac 版（60 fps、8 Mbps、最大 1920）。

## Services/

### DeviceManager.cs
對應 `DeviceManager.swift`：每 2 秒輪詢 `host:devices-l`（`DispatcherQueueTimer`，所有成員只在 UI 執行緒存取）、adb server 掛了自動 `start-server`、連線後抓 `DeviceInfo`、每台裝置一個 `AppCatalog`。找不到 adb 時狀態是 `MissingAdb`。無線裝置記住與自動重連、遠端 PhoneCentre 尚未移植。

### DeviceStatus.cs
對應 `DeviceStatus.swift`：一次 shell 取得電池（狀態碼區分充電中／已插電未充電）、預設網路（Wi-Fi 或 MOBILE[LTE]→4G/5G）、telephony.registry 的每個卡槽（電信、世代、dBm、三星 SignalBarInfo 格數、緊急通話、無線電關閉）、Wi-Fi RSSI／SSID／IP、電池循環與容量、生產日期、旋轉設定。`DeviceStatusStore` 每 10 秒更新所有在線裝置。

### AppCatalog.cs
對應 `AppCatalog.swift`：App 清單與圖示（輔助程式 `list`／`icons`，PNG 存在 `%LOCALAPPDATA%\PhoneCentre\Cache\<裝置鍵>\icons`）、已開啟／最近／目前在最上層的 App（解析 `dumpsys activity recents`、`ps -A`、`topResumedActivity`，找不到套件時用 affinity 前綴比對）、啟動、強制停止（連同 task 一起移除）、`BarOrder`／`Sections` 排序（App 列、總覽共用）。

## Pages/

### OverviewPage.cs
對應 `DeviceOverview.swift`：標題與狀態環（點一下開鏡像）、喜好項目／已開啟（右鍵：強制停止、在獨立視窗開啟、喜好項目）、五個大按鈕（鏡像畫面、桌面模式、安裝 APK、解鎖密碼、重新啟動）、裝置資訊（讀到之前只轉圈圈）、虛線拖放區（APK 安裝、照片影片存相機膠卷、其他存下載）。各區塊獨立重畫，避免整頁重建把捲動位置洗掉。

### LaunchpadPage.cs
對應 `LaunchpadView.swift`：上方「開啟方式」下拉（獨立視窗／手機主螢幕，存在設定）、膠囊搜尋、重新整理；沒搜尋時依序是喜好項目、已開啟（單排，用 `ArrowScroll`，放不下時兩端出現箭頭）、「所有 App」依 `AppIndex` 分組（A–Z → ㄅ–ㄦ → #，組內用 `NameComparer`）的格狀排列（`ItemsRepeater`＋`UniformGridLayout`，圖示 64、格寬 96）；搜尋時合併成單一格。右邊的索引列可點或拖曳跳到該組。右鍵選單：強制停止、在獨立視窗開啟、在手機主螢幕開啟、App 資訊、拷貝套件名稱、解除安裝（確認對話框）、喜好項目。
`catalog.Changed` 時用「簽章」判斷：App 清單與分區沒變就只對每個 `AppCell` 呼叫 `Refresh`（更新圖示與執行中標記），有變才重建並還原捲動位置，所以每 3 秒的狀態更新不會讓捲動位置跑掉。

### AdbSetupView.cs
找不到 adb 時的一鍵下載安裝。

## 視窗

### MainWindow.cs
左邊 236 寬的裝置側邊欄（連線方式圖示、名稱、連線資訊、Android 版本、狀態環；底部 adb server 狀態與最近事件；+ 加入無線裝置），右邊是標題加 `SelectorBar` 分頁列（總覽／啟動台／檔案／通知／簡訊／通話記錄／相機膠卷）與內容。目前總覽與啟動台有內容。分頁列是 `SegmentedBar`。測試參數 `--tab=<Launchpad|Files|…>` 直接切到某一頁。預設大小 1100×880，但不超過工作區的 92% 並置中。

### MirrorWindow.cs
左邊手機畫面（黑底，`Image`＋`WriteableBitmap`，`CompositionTarget.Rendering` 取最新幀），右邊 76 寬控制欄（`DesktopAcrylicBackdrop` 透出桌面）：
- 狀態環（點一下降下通知中心）、現在用的 App 一塊盤子、其他已開啟＋喜好項目一塊盤子（分隔線後是還沒開的；執行中右邊有小圓點，最上層的拉長成短線）。
- 導覽盤子：畫質、輸入方式、模擬定位、聲音（這四個尚未移植）、截圖（`screencap -p` 存到桌面）、旋轉選單、黑屏、電源；分隔線下是三星風格金剛三鍵（返回、主畫面、多工）。
- 輸入：左鍵＝觸控（視窗座標以 Uniform 置中縮放換算成手機座標）、右鍵＝返回、中鍵＝主畫面、滾輪＝捲動、實體按鍵依按鍵位置送 Android keycode（字母、數字、方向、Enter、Esc＝返回…；文字輸入與輸入法尚未做）。
- 第一次知道畫面大小時依比例調整視窗（高度不超過工作區 88%）。
- 與 Mac 版相比尚未有：獨立 App 視窗、桌面模式、橫向底部橫條、鎖定偵測與解鎖、音訊。

### Support/WindowManager.cs
`OpenMirror`（同一台裝置只開一個）、`Launch`、`OpenApp`（目前退化成在主螢幕開啟）。

## Ui/
- `MIcon`：Material 圖示控制項。
- `SamsungIcon`：One UI 的返回「<」、主畫面「圓角方形」、多工「|||」與強制停止（六角形加叉），照 Mac 版 `SamsungNavIcon` 的尺寸與線寬。
- `StatusIndicator`：外環＝電池（左右兩段 145°–232°、308°–395°，充電綠色、≤20% 紅色，電量數字壓在環頂端缺口），中間 Wi-Fi（依格數）或 4G/5G，下方 4 個訊號點（同一個圓上、角度間隔 22°）；沒讀到狀態時轉圈圈。
- `AppCell`：App 圖示格（圖示、名稱、執行中小圓點／最上層的短線），`Refresh` 原地更新。
- `ArrowScroll`：捲動區，放不下時在可捲動的那一端出現小箭頭，按下去捲動一頁（對應 `ArrowScroll.swift`）。
- `SegmentedBar`：分頁列（整條膠囊，選中的項目有較亮的膠囊底）。
- `UiHelpers.cs`：顏色、`Text`、`Panel`、`AppIcon`、`RunningMark`、`NavButton`（樣式在 `App.xaml` 的 `NavButtonStyle`，48×34、滑過有底色）。

## Support/ 與 Settings/
- `AppIndex.cs`／`BopomofoTable.cs`：啟動台分組鍵。`BopomofoTable` 是在 Mac 上用 `mandarinToLatin → Latin-Bopomofo` 轉換為 U+4E00–U+9FFF 每個字預先算好的注音首碼表（20992 字元），讓中文 App 名稱的分組與 Mac 版完全一致（Windows 沒有等價的 API）。
- `Shell.cs`：非同步執行外部程式（stdout／stderr 邊跑邊讀、逾時強制結束）、單引號跳脫。
- `Assets.cs`：內嵌資源解出；`CacheDir.cs`：快取目錄；`Log.cs`：`%LOCALAPPDATA%\PhoneCentre\log.txt`；`DropHandler.cs`：拖放處理。
- `AppSettings.cs`：`settings.json`（目前只有 KeepAlive、自訂 adb 路徑、各裝置的喜好項目）。

## 已知限制
- x64 版用 Windows 內建解碼器沒有畫面（見 H264Decoder），所以走 FFmpeg 軟體解碼；舊的低壓雙核 CPU（例如 i5-4278U）可以跑，但延遲與 CPU 使用率還沒量測。
- 視窗沒有最小尺寸限制。
