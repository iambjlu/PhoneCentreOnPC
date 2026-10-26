# 第三方元件聲明

## scrcpy-server

PhoneCentre 內含未經修改的 scrcpy 官方 release 檔案 `scrcpy-server`（v3.3.4），
在裝置端以 `app_process` 執行，負責擷取畫面與注入輸入事件。
Windows 端的用戶端（解碼、顯示、操控協定）由 PhoneCentre 以 C# 獨立實作，未使用 scrcpy 用戶端程式碼。

- 專案：https://github.com/Genymobile/scrcpy
- Copyright (C) 2018 Genymobile
- Copyright (C) 2018-2025 Romain Vimont
- 授權：Apache License 2.0，全文見 `licenses/scrcpy-LICENSE.txt`

## FFmpeg（libavcodec、libavutil、libswscale）

PhoneCentre 以動態連結（共享庫 DLL）使用 FFmpeg 7.1.1 的 H.264 解碼器與像素格式轉換，未修改 FFmpeg 原始碼。
DLL 由 `scripts/build-ffmpeg.sh` 從官方原始碼（https://ffmpeg.org/releases/ffmpeg-7.1.1.tar.xz）編譯，
只啟用 h264 解碼器／parser 與 swscale，**LGPL 版本**（沒有 `--enable-gpl`、沒有 `--enable-nonfree`）。

- 專案：https://ffmpeg.org
- 授權：GNU Lesser General Public License v2.1 或更新版本，全文見 `licenses/FFmpeg-LGPLv2.1.txt`
- 使用者可自行用 `scripts/build-ffmpeg.sh` 重新編譯並取代 `Native/` 下的 DLL。

## Material Icons

介面圖示使用 Google Material Icons（Round 樣式）的向量路徑（`Ui/MaterialIconData.cs`）。

- 專案：https://github.com/google/material-design-icons
- 授權：Apache License 2.0（全文與 scrcpy 相同，見 `licenses/scrcpy-LICENSE.txt`）

## Android SDK Platform-Tools（adb）

PhoneCentre **不內含** adb。若系統找不到 adb，PhoneCentre 會在使用者同意後，
從 Google 官方網址（https://dl.google.com/android/repository/platform-tools-latest-windows.zip）
下載 Android SDK Platform-Tools 到 `%LOCALAPPDATA%\PhoneCentre\platform-tools`。
Platform-Tools 受 Android Software Development Kit License Agreement 約束：
https://developer.android.com/studio/terms

## Inter

介面英文字型使用 Inter（SIL Open Font License 1.1，全文見 `licenses/Inter-OFL.txt`）。https://github.com/rsms/inter

## 台北黑體 Taipei Sans TC

介面中文字型使用台北黑體（開源字型，SIL Open Font License 1.1），只內嵌用到的字元子集。https://sites.google.com/view/jtfoundry/zh-tw

## Leaflet

位置模擬器的地圖使用 Leaflet（BSD 2-Clause，全文見 `licenses/Leaflet-BSD2.txt`），地圖圖磚來自 OpenStreetMap contributors（ODbL）。https://leafletjs.com
