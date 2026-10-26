# 專案規則

- 請使用中文（繁體）進行回應
- 做事之前先根據指令，列出 todo list 再一步一步執行
- 如果我指出有發生問題，但是描述不夠清楚的話，則你先執行過一遍先了解問題，真的不了解才來問我詳細資訊

## 這個專案是什麼

PhoneCentre 的 Windows 版（WinUI 3，C#／.NET 10），功能與外觀盡量對照 Mac 版（`../PhoneCentre`，Swift）。
Mac 版是功能與行為的參考：要移植某個功能時，先讀 Mac 版對應的 Swift 檔再寫 C#。
目標是**使用者雙擊就能用的單一 exe**（內含 .NET、Windows App SDK、FFmpeg、scrcpy-server，免安裝），給 Intel（x64）PC 使用；ARM64 順便支援。

## 更動規範

在開始進行更動前，請先閱讀 `Readme_Architecture.md` 了解專案架構，如果真的找不到才用 grep 一個一個搜尋。

在此專案中做任何更動（程式碼、設定、架構等），都必須：

1. 將變更內容同步更新到 `Readme_Architecture.md`（直接寫入對應程式檔所屬的章節裡面，不要獨立出來變成新章節，並且不要提及是何時的何計劃）。
2. 建立 git commit（原始更動 + Readme 更新一起 commit，或分開 commit 皆可）。

此步驟不可省略。**不要把密碼、金鑰寫進任何檔案或 commit。**

## 設計決定（已和使用者確認）

- 不做 Liquid Glass，視窗用 Mica／Acrylic 或灰底；對 Windows 的視覺效果沒有很高期待。
- SF Symbols 改用 Material Icons（向量路徑編進程式，見 `Ui/MaterialIconData.cs`）；**返回／主畫面／多工金剛三鍵沿用三星 One UI 風格**（`Ui/SamsungIcons.cs`）。
- 視訊用 FFmpeg 軟體解碼（和 scrcpy 一樣）。Windows 內建的 Media Foundation 解碼在 x64 上對這個串流沒有輸出，不要改回去。
- 全部用深色主題。

## 開發環境與機台

- 開發在 Mac 上寫程式；**編譯在 Windows on ARM 機台**（快），**驗證在 Intel 機台**（慢，但是真正的目標平台）。
  - ARM 建置機：`10.211.55.10`，帳號 `iambjlu`（Parallels 虛擬機，x64 版在這台是靠模擬執行）。
  - Intel 機台：`192.168.31.68`，帳號 `Administrator`（i5-4278U，真機）。
  - 密碼由使用者在對話中提供，**用環境變數 `SSHPASS` 傳給腳本**，不要寫進檔案。
- `scripts/deploy.sh [x64|arm64]`：把專案同步到 ARM 機台並發行單一 exe（輸出在機台的 `C:\Users\iambjlu\src\publish\<平台>\PhoneCentre.exe`）。
- `scripts/shot.sh <arm|intel> <exe 的 Windows 路徑> [exe 參數] [等待秒數] [手機位址]`：在機台的桌面會話啟動 exe、截圖、拉回 Mac（`--mirror` 參數會自動開鏡像視窗）。
- 測試手機：三星 S10（SM-G973F）`192.168.31.152:5555`，Mac 的 adb 金鑰（`~/.android/adbkey*`）已複製到兩台 Windows 機台，不用再授權。
- 兩台機台上的 adb（`%LOCALAPPDATA%\PhoneCentre\platform-tools`）第一次執行會跳 Windows 防火牆詢問，要在機台上按「允許」；沒回應會被自動建立阻擋規則，之後一直跳。
- 存放位置：
  - **Mac 桌面不要放東西**（會滿出來）。Mac 上的暫存與輸出放外接硬碟 `/Volumes/MX500_APFS/tmp/PhoneCentre-Windows/`（`build`、`out`、`ffmpeg`、`icons`）。
  - Windows 的成品放該機台桌面的 `PhoneCentre-Windows` 資料夾。
- `.ps1` 檔案要存成 **UTF-8 with BOM**，否則中文注解會讓 Windows PowerShell 5.1 解析錯誤。
- 不要截 Mac 的全螢幕（會拍到不相干的內容）。

## 完成後編譯與測試流程

每次任務完成後，必須執行：

- 說明有沒有需要重新編譯哪個程式、需不需要重新開。
- 用 `scripts/deploy.sh x64` 重新編譯，確認沒有錯誤；用 `scripts/shot.sh` 在 ARM 機台截圖確認畫面；涉及視訊、輸入的變更要再到 Intel 機台驗證。
- 如果是參考 md 進行的任務，請在最後回頭檢視各項任務是否皆已確實完成或是有無新的任務（如有要求做完打 ✅，則先重新讀取有無新內容再打 ✅）。

以上全部無誤才算完成。
