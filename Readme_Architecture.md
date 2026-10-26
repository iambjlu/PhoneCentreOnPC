# PhoneCentreOnPC 架構說明

PhoneCentre 的 Windows 版（WinUI 3，C# / .NET 10 / Windows App SDK 1.8）。透過 adb 連線手機，用 scrcpy-server 做畫面鏡像與遠端操作；
功能與外觀對照 Mac 版（`../PhoneCentre`）。手機端（scrcpy-server、`phonecentre-helper.jar`）與 Mac 版共用，原封不動。

- 目標：使用者雙擊就能用的**單一 exe**（約 94 MB，內含 .NET 執行階段、Windows App SDK、精簡 FFmpeg、scrcpy-server、手機端輔助程式），免安裝。
- 平台：Windows 10 1809 以上；x64（Intel／AMD）為主，ARM64 順便支援。
- 還沒移植的功能：通知、簡訊、通話記錄、相機膠卷、設定視窗、遠端 PhoneCentre（暫時不做）、桌面模式、獨立 App 視窗、模擬定位、聲音、畫質設定、輸入法、解鎖密碼、系統匣、無線裝置自動重連。介面上這些按鈕與分頁已經在，點了會顯示「尚未移植」。

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

## 字型與圖示

- 字型：英數字用 Inter、中文用台北黑體 Beta（翰字鑄造，改編自思源黑體；兩者都是 SIL OFL 1.1）。`Fonts/` 是 Inter 的 Regular／Medium／SemiBold／Bold 與台北黑體的 Regular／Bold（台北黑體用 `scripts/subset-taipei-sans.py` 裁成 Big5＋GB2312＋注音＋假名＋標點，每檔約 4.9 MB，不然完整字型要 20 MB）。全域字型是 `Inter, Taipei Sans TC Beta`（`Fonts.Apply` 設 `ContentControlThemeFontFamily` 與 `TextBlock` 預設樣式）。
  - WinUI 的文字走 DirectWrite 的系統字型集合，只認系統字型或 `ms-appx` 檔案；單一 exe 旁邊沒有字型檔，而用 `AddFontResourceEx(FR_PRIVATE)` 登記的程序專用字型 DirectWrite 看不到（實測過，Inter 和台北黑體都沒生效）。所以 `Fonts.Install` 在 `App` 建構一開始（XAML 第一次用到字型之前）把字型暫時安裝成「目前使用者」的字型（複製到 `%LOCALAPPDATA%\Microsoft\Windows\Fonts`、登記在 `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts`、`AddFontResource`＋廣播 `WM_FONTCHANGE`，不需要管理員權限），程式結束（`ProcessExit`）時移除；被強制結束的話字型會留著，下次啟動會覆蓋。
  - `--fonttest` 開字型比對視窗（`FontTestWindow`）。
- App 圖示：設計同 Mac 版（綠到藍漸層圓角方塊、白色手機、四個彩色 App 方塊），`scripts/make-icon.swift` 產生各尺寸 PNG（外框留白 3%，比 Mac 的 10% 小，工作列上才夠大）、`scripts/pack-ico.py` 打包成 `Resources/AppIcon.ico`（16–256 共 10 個尺寸）。`<ApplicationIcon>` 給 exe，內嵌一份給 `WindowIcon.Apply`（`AppWindow.SetIcon`）套到每個視窗與工作列按鈕。
- Material 圖示：`scripts/gen-icons.py` 產生 `Ui/MaterialIconData.cs`（要新增圖示就把名稱加進 `NAMES`；`<circle>` 也會轉成路徑）。狀態環中央的 Wi-Fi 用 Material 的 `wifi`（淡的完整圖示打底）疊目前格數的亮圖示（`wifi_1_bar`／`wifi_2_bar`／`wifi`）。

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

### PhoneData.cs
對應 `PhoneData.swift`＋`PhoneDataStore.swift`：`PhoneSMS`／`PhoneCall`／`PhoneNotification`／`PhoneDataBundle`（JSON 存 `%LOCALAPPDATA%\PhoneCentre\Data\<裝置鍵>\data.json`，先寫暫存檔再取代，不放快取目錄所以不會被容量清理刪掉）。`PhoneDataParser`：`Rows` 解析 `content query` 輸出（每筆以「Row: N 」開頭，值可能含逗號與換行，所以依投影欄位的固定順序切割，最後一欄可含任何內容）；`Notifications` 解析 `dumpsys notification --noredact` 的「Notification List」區段（`NotificationRecord(` 取 pkg／key／flags，0x2＝常駐；extras 的 title／text／bigText，值含換行時累積到行尾的「)」；找不到區段回傳 null，避免把所有通知誤判成已清除）。`PhoneDataStore`：每 15 秒一次，通知每次都同步、簡訊與通話記錄每分鐘一次，只抓比已保存最新一筆更新的；通知對照手機目前的（不見的標 `ClearedAt`、新的加入、同一個 key 內容有變就原地更新）；`Prune` 依保存天數刪除（還在手機上的通知不刪）。

### ThumbnailLoader.cs
對應 `ThumbnailLoader`：格子出現才排隊，80 ms 內的請求合併成一批（24 張）交給手機端 `thumbs 256`，只傳小圖不傳原檔；結果存到 `Cache\<裝置鍵>\thumbs`（鍵含路徑、大小、修改時間與縮圖版本 v2）。判斷佇列是否空與清除執行旗標要在 UI 執行緒一起做。

### RemoteFS.cs
對應 `RemoteFiles.swift`：`RemoteFile`（路徑、名稱、是否資料夾、大小、修改時間；種類名稱、圖示名稱、是否圖片／影片）、`RemoteFS`（`stat -L -c '%F|%s|%Y|%n'` 列目錄與遞迴列出、mkdir、`mv -n`、`rm -rf`＋媒體掃描、下載到快取並以路徑雜湊分資料夾）、`FileSize.Format`（十進位，像 macOS 的 ByteCountFormatter）。

### AppCatalog.cs
對應 `AppCatalog.swift`：App 清單與圖示（輔助程式 `list`／`icons`，PNG 存在 `%LOCALAPPDATA%\PhoneCentre\Cache\<裝置鍵>\icons`）、已開啟／最近／目前在最上層的 App（解析 `dumpsys activity recents`、`ps -A`、`topResumedActivity`，找不到套件時用 affinity 前綴比對）、啟動、強制停止（連同 task 一起移除）、`BarOrder`／`Sections` 排序（App 列、總覽共用）。

## Pages/

### OverviewPage.cs
對應 `DeviceOverview.swift`：標題與狀態環（點一下開鏡像）、喜好項目／已開啟（右鍵：強制停止、在獨立視窗開啟、喜好項目）、五個大按鈕（鏡像畫面、桌面模式、安裝 APK、解鎖密碼、重新啟動）、裝置資訊（讀到之前只轉圈圈）、虛線拖放區（APK 安裝、照片影片存相機膠卷、其他存下載）。各區塊獨立重畫，避免整頁重建把捲動位置洗掉。

### LaunchpadPage.cs
對應 `LaunchpadView.swift`：上方「開啟方式」下拉（獨立視窗／手機主螢幕，存在設定）、膠囊搜尋、重新整理；沒搜尋時依序是喜好項目、已開啟（單排，用 `ArrowScroll`，放不下時兩端出現箭頭）、「所有 App」依 `AppIndex` 分組（A–Z → ㄅ–ㄦ → #，組內用 `NameComparer`）的格狀排列（`ItemsRepeater`＋`UniformGridLayout`，圖示 64、格寬 96）；搜尋時合併成單一格。右邊的索引列可點或拖曳跳到該組。右鍵選單：強制停止、在獨立視窗開啟、在手機主螢幕開啟、App 資訊、拷貝套件名稱、解除安裝（確認對話框）、喜好項目。
`catalog.Changed` 時用「簽章」判斷：App 清單與分區沒變就只對每個 `AppCell` 呼叫 `Refresh`（更新圖示與執行中標記），有變才重建並還原捲動位置，所以每 3 秒的狀態更新不會讓捲動位置跑掉。

### FilesPage.cs
對應 `FileBrowserView.swift`：左邊「位置」（內部儲存空間、下載、相機、圖片、文件、音樂、影片、暫存、根目錄）；上方上一頁／下一頁／上一層、可點的路徑列、顯示隱藏檔、新增資料夾、上傳、下載、重新整理；表格欄位名稱／修改日期／大小／種類，點標題排序（資料夾一律在前面，名稱用 `NameComparer`）；底部「N 個項目」與狀態。雙擊開啟（資料夾進入；檔案先下載到快取再用預設程式開啟）、右鍵（打開、重新命名、拷貝路徑、下載、刪除；空白處：新增資料夾、上傳）、Delete 鍵刪除（確認對話框）。拖檔進來上傳（資料夾也行）；拖出到檔案總管時用 `SetDataProvider` 延遲提供，放開才 `adb pull` 到暫存。載入有序號，切換資料夾時舊的結果會被丟掉。沒有 Quick Look（Windows 沒有對應物），改成直接開啟。圖示用 Material（檔案類型對應在 `RemoteFile.IconName`）。

### PhotosPage.cs
對應 `CameraRollView.swift`：上方相簿下拉（所有照片＝DCIM 底下遞迴、相機、DCIM 與 Pictures 的各資料夾）、全部／照片／影片、項目數、已選取數與匯出／刪除、縮放滑桿（80–280，`UniformGridLayout` 的格子大小）、重新整理。格子是 `PhotoCell`（縮圖、影片的播放標記、選取框與勾勾），用 `ItemsRepeater`＋`PhotoCellFactory`（回收格子）虛擬化；`ElementPrepared`／`ElementClearing` 對應 Mac 版的 onAppear／onDisappear，通知 `ThumbnailLoader` 該排隊或取消。選取：Ctrl 點選加入或取消、Shift 選取範圍、Ctrl＋A 全選、Delete 刪除；右鍵（打開、匯出、刪除）；拖檔進來匯入（「所有照片」匯入到 DCIM/Camera）；拖出到檔案總管用 `RemoteDrag`。匯出遇到同名檔案會加編號。雙擊開 `MediaViewerWindow`（照片顯示原圖、影片用 `MediaPlayerElement`，左右鍵切換，Esc 關閉）。

### PhoneDataPage.cs
對應 `PhoneDataView.swift`＋`PhoneSMSView.swift`：通知／簡訊／通話記錄三個分頁共用一個類別（`DataKind` 區分）。沒開啟收集時顯示說明卡片與「開啟」按鈕（預設關閉，因為是個人隱私資料）。標題列：通知有「現在／所有」與「顯示常駐通知」；右邊是保存期限、搜尋（空間不夠會縮）、立即更新（更新中在按鈕原位轉圈）、更多（停止收集、刪除已保存的資料）。
- 通知：一塊面板，列與列之間有分隔線；App 圖示與名稱來自該裝置的 `AppCatalog`；已清除的標示清除時間；點一則會詢問要不要打開該 App。
- 通話記錄：撥入／撥出／未接（紅色）圖示、號碼與種類、時間與時長。
- 簡訊：左邊依號碼分組的對話列表（最後一則兩行預覽、選中的用強調色），右邊是氣泡對話（收到灰色靠左、寄出綠色靠右，間隔超過 1 小時插入日期分隔，切換對話時捲到最新一則）。
- 裝置鍵優先用 ro.serialno，所以手機資訊還沒讀到之前頁面只轉圈圈、`SyncAsync` 也直接返回，否則資料會存到用連線位址當鍵的另一個目錄。資料沒變就不重畫（重畫會洗掉捲動位置）。
- 測試參數：`--enable-phonedata` 開啟收集、`--no-sync` 只顯示已保存的資料（用假資料截圖時不會又把真實資料收進來）。

### AdbSetupView.cs
找不到 adb 時的一鍵下載安裝。

## 視窗

### MainWindow.cs
左邊 236 寬的裝置側邊欄（連線方式圖示、名稱、連線資訊、Android 版本、狀態環；底部 adb server 狀態與最近事件；+ 加入無線裝置），右邊是標題加 `SelectorBar` 分頁列（總覽／啟動台／檔案／通知／簡訊／通話記錄／相機膠卷）與內容。目前總覽與啟動台有內容。分頁列是 `SegmentedBar`。測試參數 `--tab=<Launchpad|Files|…>` 直接切到某一頁。預設大小 1100×880，但不超過工作區的 92% 並置中。

### Mirror/MirrorModel.cs
對應 `MirrorModel.swift`：一個鏡像視窗的狀態與工作階段管理，只在 UI 執行緒使用。主螢幕鏡像，或是在虛擬顯示器上執行單一 App（獨立視窗）／桌面（桌面模式）。
- 狀態（連線中、提示、組字文字、手機鎖定、黑屏、靜音、鍵盤模式、畫質、影像大小、手機狀態、App 已關閉與最後一幀）改了都會觸發 `Changed`，視窗再重畫疊加層與右側欄。
- 連線：`Start` 建立 `ScrcpySession`＋`H264Decoder`＋（主螢幕才有）`AudioPlayer`，每個 session 一組 `Link`；重啟時先拔掉舊 session 的回呼，延遲 0.6 秒等舊 server 釋放硬體編碼器；斷線時 `ScheduleRetry` 每 3 秒看裝置回來沒有（自動維持連線）。
- 虛擬顯示器：大小＝視窗 DIP × 螢幕縮放（密度 160 × 縮放，1 DIP ≈ 1 dp，取 8 的倍數，受最大解析度限制）；連線後 `FinishConnectAsync` 以 `dumpsys display` 找名為 scrcpy、大小相符且尚未被認領的顯示器；調整視窗大小時 `Start(handover: true)` 保留舊 session，新顯示器就緒後用 `am display move-stack` 把舊顯示器的 root task 搬過去（確認舊顯示器沒有 task 才關舊 session）；`NudgeDisplayConfigAsync` 把密度加 1 再改回觸發 App 重新排版（先確認顯示器還在，否則 `wm density -d` 會退回操作主螢幕）；`EnsureAppOnDisplayAsync` 處理 App 本來就在別的顯示器執行時畫面黑掉；`SendAppsToBackgroundAsync` 關視窗前用手機端輔助程式 `tobg` 把 App 送到主螢幕背景再 `am kill`。
- 獨立視窗每 2 秒檢查顯示器上是否還有 root task（連續兩次沒有才算關閉），顯示「XXX 已關閉」與「打開」「關閉視窗」；使用者在手機上自己重開時會自動搬回來。
- 定時：剪貼簿（`Clipboard.ContentChanged` 標記後每 0.7 秒同步到手機）、手機狀態每 5 秒、App 列與鎖定偵測每 3 秒。
- 操作：導覽鍵（虛擬顯示器上用 `input -d <id> keyevent`）、旋轉／自動旋轉／強制方向、黑屏、靜音、截圖（白閃；主螢幕 `screencap -p`，虛擬顯示器或失敗時取鏡像關鍵幀，用系統 `BitmapEncoder` 編成 PNG）、套用畫質（重啟）、拖放檔案。

### Mirror/MirrorView.cs
對應 `MirrorView.swift`：顯示畫面（`Image`＋`WriteableBitmap`，`CompositionTarget.Rendering` 取目前解碼器的最新幀）並把輸入轉成 scrcpy 控制訊息。左鍵＝觸控、右鍵＝返回、中鍵＝主畫面；一般滑鼠滾輪送捲動事件；觸控板（`PointerDeviceType.Touchpad`）兩指捲動模擬單指拖曳（移動超過 6 px 才放下手指，110 ms 沒有事件就放開，讓 Android 自己算慣性）；Ctrl＋滾輪（含觸控板捏合）以指標為中心斜向擺兩指縮放。鍵盤用 `PreviewKeyDown`：Ctrl＋V／C／X／A／Z 是剪貼簿、全選、復原；其餘依 `KeyMap` 送 keycode（含修飾鍵 meta 與重複）。
- 輸入法：「Windows 輸入法」模式時，文字鍵（字母、數字、符號）交給畫面上一個看不見的 `TextBox`（Windows App SDK 沒有 WinUI 版的 `CoreTextEditContext`，所以用 TextBox 當橋接，組字與候選窗由它原生處理，候選窗出現在畫面下方中央）；`TextCompositionChanged` 讀組字文字顯示在畫面下方，提交的文字：ASCII 用 `INJECT_TEXT`，中文等非 ASCII 透過手機剪貼簿貼上（貼上前記住電腦剪貼簿，貼完 0.8 秒再放回，期間 `ClipboardGuardUntil` 不讓手機回傳的剪貼簿蓋掉電腦的）。「實體按鍵」模式所有按鍵直接送 keycode。
- 拖放檔案進來交給 `MirrorModel.Drop`（APK 安裝、照片影片存相機膠卷、其他存下載）。

### Mirror/SideColumn.cs
對應 `NavBar.swift` 的 `SideColumn`：寬 76 的右側欄，由上到下：狀態環（點一下降下通知中心）、「現在用的 App」一塊盤子（完整顯示）、「其他」一塊盤子（已開啟的 → 分隔線 → 還沒開的喜好項目；放不下有箭頭可以捲動）、導覽盤子（導覽項目可捲動；金剛三鍵固定在最下面）。空間分配照 Mac 版：導覽項目優先，「其他」盤子最少留三個圖示，由 `Reallocate` 量測各塊的自然高度後設定高度。橫向且視窗內容高度未滿 608 時整排移到視窗底部橫向排列。獨立視窗沒有 App 盤子，現用的 App 圖示與名稱放在欄位正中間（橫向時在狀態環與導覽盤子之間那塊空白的正中間），導覽項目沒有定位，金剛三鍵換成「隱藏視窗」與「強制停止並關閉視窗」。App 已關閉時導覽盤子反灰不能按。
- 執行中標記：垂直在右邊、水平在下面，目前在最上層的拉長成圓角短線。
- 把 App 圖示往左或往右拖出 App 列超過 70 px 放開 → 在放開位置以獨立視窗開啟；拖曳時 `DragGhost`（透明置頂、不搶焦點、滑鼠穿透的小視窗）顯示跟著游標的半透明圖示，超過距離時右下角出現綠色加號。右鍵：強制停止、在獨立視窗開啟、喜好項目。
- 導覽項目：畫質設定、輸入方式（選單）、桌面模式的小型啟動台、模擬定位（模擬中換圖示）、聲音、截圖、旋轉（選單：四個方向、自動旋轉、強制所有 App 使用這個方向；虛擬顯示器只有「旋轉視窗方向」）、黑屏、電源。按鈕都有無障礙名稱（UI Automation 測試也靠它）。
- 重建用「簽章」避免閃爍：App 清單與圖示載入、導覽狀態沒變就不重建（重建導覽按鈕會把開著的彈窗關掉）。

### Mirror/Popovers.cs
對應 `VideoSettingsPopover`、`SoundPopover`、`MiniLauncher`、`LockPasswordSheet`：
- 畫質：省流量／一般／高畫質／自訂預設、影格率、解析度上限、位元率滑桿，套用後重啟連線。
- 聲音：媒體音量滑桿（`cmd media_session volume --stream 3`，超過 120 變紅並出現警告）、一鍵靜音、從電腦播放／從手機播放（切換後重新連線，需要 Android 13）、鈴聲模式（依序嘗試 `cmd audio set-ringer-mode`、`service call audio 38`、鈴聲音量，每次都回頭確認）、請勿打擾。
- 桌面模式的小型啟動台（搜尋＋圖示格）。
- 解鎖密碼對話框：PIN／密碼或 3×3 圖形（`PatternPad`，Beta），存在 `SecretStore`。

### Mirror/LockScreenHelper.cs
對應 `LockScreenHelper.swift`：用 `dumpsys window` 的 keyguard 欄位判斷鎖定；解鎖流程：Windows Hello（`UserConsentVerifier`；沒設定 Hello 的電腦退回確認對話框）→ 喚醒 → 黑屏（只關面板）→ 上滑 → 輸入密碼或畫圖形（圖形範圍用 `uiautomator dump` 找 `LockPatternView`，找到過的快取，失敗就清掉重找；有鏡像連線直接送觸控，沒有就用手機端 `gesture`）→ 驗證。

### Mirror/LocationSimulator.cs
對應 `LocationSimulator.swift`：`LocationSimulator` 用 `cmd location providers` 的測試提供者（gps、network）套用／重送／移除；`LocationSimulation` 每 5 秒重送位置維持模擬（跟視窗開不開無關，只有「停止模擬」才結束）。`LocationSimulatorWindow` 用 WebView2 顯示 Leaflet＋OpenStreetMap（頁面與 Leaflet 解到 `%LOCALAPPDATA%\PhoneCentre\map`，用虛擬主機名稱 `map.phonecentre.local` 載入，OSM 圖磚才會帶 Referer；WebView2 的使用者資料放在 `%LOCALAPPDATA%\PhoneCentre\WebView2`），點地圖選位置，搜尋用 Nominatim。需要系統有 WebView2 執行階段（Windows 11 內建）。

### Mirror/AudioPlayer.cs
對應 `AudioPlayer.swift`：NAudio 的 WASAPI 共用模式（60 ms）播 48 kHz 雙聲道 s16le，`BufferedWaveProvider` 緩衝超過 200 ms 就清掉維持低延遲；`Volume` 靜音用。

### Mirror/KeyMap.cs
Windows 虛擬按鍵 → Android keycode（字母、數字、符號、Enter／Backspace／Delete／Tab／空白、Esc＝返回、方向鍵、Home／End／PageUp／PageDown／Insert、F1–F12、數字鍵盤），以及修飾鍵 → meta state；輸入法模式下哪些是「文字鍵」交給輸入法。

### MirrorWindow.cs
對應 `MirrorWindowController.swift`＋`MirrorContentView.swift`：視窗（Mica）與疊加層。
- 版面：影像區（黑底）＋右側欄，橫向且內容高度未滿 608 時右側欄移到底部（高 64）。
- 疊加訊息（毛玻璃底卡片）：手機已鎖定橫幅（主螢幕往下留 49 不擋手機狀態列；有存密碼顯示「用 Windows Hello 解鎖」，沒有顯示「儲存解鎖密碼…」；可關掉）、組字文字、App 已關閉（最後一幀＋Acrylic 霧化，卡片有圖示與「打開」「關閉視窗」）、連線中／變更中提示、畫面中央的提示訊息、截圖白閃（500 ms 淡出）。
- 視窗大小：第一次收到影像依手機比例調整（高度不超過工作區 85%）、旋轉時保持較長邊；獨立視窗與桌面模式的虛擬顯示器依視窗大小建立，使用者調整過後不再反過來改視窗；記住位置與大小（`SettingsData.WindowFrames`），重新開啟時還原；關閉虛擬顯示器視窗前先 `SendAppsToBackgroundAsync`。
- `WindowSubclass`（SetWindowSubclass）：最小大小（直向 364×636、橫向 636×364，含標題列；主螢幕鏡像看手機畫面比例、虛擬顯示器看視窗形狀）、主螢幕鏡像縮放時維持影像比例（`WM_SIZING`，右側欄寬或底部橫條高是固定的額外尺寸）、放開邊框時依新大小重建虛擬顯示器（`WM_EXITSIZEMOVE`）。

### Support/WindowManager.cs
對應 `WindowManager.swift`：以「裝置＋App／桌面／主螢幕」為鍵，同一個只開一個視窗。`OpenMirror`（主螢幕鏡像）、`OpenApp(device, package, at)`（獨立視窗，`at` 是視窗左上角的螢幕位置）、`OpenDesktop`、`Launch`（依啟動台的開啟方式）、`LaunchOnMainScreen`、`OpenLocation`、`OpenSettings`。

### Support/TrayIcon.cs ＋ Pages/TrayPopup.cs
對應 `MenuBarController`／`MenuBarView`。`TrayIcon` 用 `Shell_NotifyIcon`，回呼訊息掛在主視窗 `WindowSubclass.MessageHook`（也處理檔案總管重啟的 `TaskbarCreated`）；是否顯示跟 `ShowTrayIcon` 設定同步。點圖示叫出 `TrayPopup`（360 寬、無邊框、置頂、失去焦點就收起，放在游標附近且不超出工作區）：每台已連線裝置一區（綠點、名稱、型號、鏡像按鈕，下面最多 14 個「執行中＋最近」App 圖示，點了依啟動台設定開啟），底部「打開 PhoneCentre」、設定、結束。沒有裝置顯示「沒有已連線的裝置」。測試參數 `--tray` 啟動後自動叫出。

### 程式生命週期（App.xaml.cs）
關閉主視窗：系統匣圖示顯示中就只是隱藏（程式留在系統匣，鏡像視窗不受影響），圖示關閉時就結束程式。`App.Quit()` 先呼叫 `WindowManager.ShutdownAsync()`（各鏡像視窗 `PrepareForQuitAsync`，最多等 6 秒，再 `Stop`）才離開。

## Ui/
- `MIcon`：Material 圖示控制項。
- `SamsungIcon`：One UI 的返回「<」、主畫面「圓角方形」、多工「|||」與強制停止（六角形加叉），照 Mac 版 `SamsungNavIcon` 的尺寸與線寬。
- `StatusIndicator`：外環＝電池（左右兩段 145°–232°、308°–395°，充電綠色、≤20% 紅色，電量數字壓在環頂端缺口），中間 Wi-Fi（依格數）或 4G/5G，下方 4 個訊號點（同一個圓上、角度間隔 22°）；沒讀到狀態時轉圈圈。
- `AppCell`：App 圖示格（圖示、名稱、執行中小圓點／最上層的短線），`Refresh` 原地更新。
- `ArrowScroll`：捲動區，放不下時在可捲動的那一端出現小箭頭，按下去捲動一頁（對應 `ArrowScroll.swift`）。
- `SegmentedBar`：分頁列（整條膠囊，選中的項目有較亮的膠囊底）。
- `UiHelpers.cs`：顏色、`Text`、`Panel`、`AppIcon`、`RunningMark`、`NavButton`（樣式在 `App.xaml` 的 `NavButtonStyle`，48×34、滑過有底色）。

## 設定

### Settings/AppSettings.cs
對應 `AppSettings.swift`：`SettingsData`（保持連線、記住無線裝置、工作列模式、啟動台開啟方式、鍵盤模式、畫質與每台裝置的畫質、音訊、剪貼簿、鏡像時黑屏、系統匣圖示、遠端開關、無線端點、遠端主機、快取上限與清除時機、Windows Hello 解鎖、手機資料收集與保存天數、各裝置喜好項目、密碼同步）。預設存 `%LOCALAPPDATA%\PhoneCentre\settings.json`；可選一個資料夾（例如 OneDrive）改存那裡，每 3 秒偵測其他電腦改過的檔案並重新載入。資料夾路徑與自訂 adb 路徑只存本機（`local.json`）。讀檔時缺少的欄位用預設值，舊設定檔也能讀。改設定用 `AppSettings.Update(d => …)`（自動存檔並觸發 `Changed`）。
### Settings/SecretStore.cs
對應鑰匙圈：密碼（手機解鎖密碼、分享密碼、遠端主機密碼）用 DPAPI（目前使用者）加密存在 `secrets.json`；開啟「密碼也存到這個資料夾」時另外以明碼寫進 `settings.json` 的 `Secrets`，另一台讀到後存回自己的 DPAPI。
### Services/CacheManager.cs
對應 `CacheManager.swift`：快取在 `Cache\<裝置鍵>\{icons,thumbs,files}`；清除時機（斷線立即／幾天沒連線／只依容量）與容量上限（LRU，取存取與寫入時間較晚的，因為 NTFS 常常不更新存取時間，刪到上限的 90%），每分鐘檢查一次；`last-seen.json` 記每台裝置最後連線時間。
### Pages/SettingsWindow.cs
設定視窗（一般、連線、鏡像、儲存四頁，頂端用 `SegmentedBar`；遠端那一頁暫時不做所以先拿掉）。用 `Ui/Form.cs` 的分組表單零件（標題＋圓角卡片、每列以分隔線隔開、`ToggleSwitch`／`ComboBox`／`Slider`／`NumberBox`）。`Ctrl+,` 或側邊欄底部的齒輪開啟；最小大小 640×520。`--settings=<頁號>` 直接開某一頁。
- 一般：系統匣圖示、工作列模式（鏡像視窗／獨立 App 視窗：整個程式共用一個或每個視窗各自一個）、啟動台點 App 的開啟方式。
- 連線：保持連線、記住無線裝置、記住的無線裝置清單（忘記）、目前使用的 adb、自訂路徑、下載／更新 Platform-Tools。
- 鏡像：預設畫質（FPS、Bitrate、最大解析度）、鍵盤模式（實體按鍵／Windows 輸入法）、剪貼簿同步、音訊（傳到電腦、手機靜音）、鏡像時黑屏、Windows Hello 解鎖。
- 儲存：設定檔位置、密碼同步、手機資料收集與各自的保存天數、快取用量與上限與清除時機、立即套用、清除全部。

## Support/ 與 Settings/
- `AppIndex.cs`／`BopomofoTable.cs`：啟動台分組鍵。`BopomofoTable` 是在 Mac 上用 `mandarinToLatin → Latin-Bopomofo` 轉換為 U+4E00–U+9FFF 每個字預先算好的注音首碼表（20992 字元），讓中文 App 名稱的分組與 Mac 版完全一致（Windows 沒有等價的 API）。
- `RemoteDrag.cs`：把手機檔案拖出到檔案總管（`SetDataProvider` 延遲提供，放開才 `adb pull`），檔案頁與相機膠卷共用。
- `Shell.cs`：非同步執行外部程式（stdout／stderr 邊跑邊讀、逾時強制結束）、單引號跳脫。
- `Assets.cs`：內嵌資源解出；`CacheDir.cs`：快取目錄；`Log.cs`：`%LOCALAPPDATA%\PhoneCentre\log.txt`；`DropHandler.cs`：拖放處理。
- `Fonts.cs`、`WindowIcon.cs`：字型安裝與視窗圖示（見上面「字型與圖示」）。

## 已知限制
- x64 版用 Windows 內建解碼器沒有畫面（見 H264Decoder），所以走 FFmpeg 軟體解碼；舊的低壓雙核 CPU（例如 i5-4278U）可以跑，但延遲與 CPU 使用率還沒量測。
- 視窗沒有最小尺寸限制。
