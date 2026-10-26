# 在 Windows 的桌面會話裡啟動 PhoneCentre、等畫面出來、截全螢幕。由 scripts/shot.sh 以互動式排程工作執行。
param([string]$Exe, [string]$Arguments = '', [int]$Wait = 20, [string]$Phone = '', [string]$Click = '')
$adb = "$env:LOCALAPPDATA\PhoneCentre\platform-tools\adb.exe"
if ($Phone) { & $adb connect $Phone *> $null }
Get-Process PhoneCentre -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\PhoneCentre\log.txt", "$env:USERPROFILE\shot.done" -ErrorAction SilentlyContinue
$err = ''
try { $p = if ($Arguments) { Start-Process $Exe -ArgumentList $Arguments -PassThru -ErrorAction Stop } else { Start-Process $Exe -PassThru -ErrorAction Stop } }
catch { $err = $_.Exception.Message; $p = $null }
Start-Sleep $Wait
# 用 UI Automation 依名稱點按鈕（用逗號分隔的名稱依序點，每次點完等 2 秒），測試彈窗與選單用
if ($Click) {
  Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
  foreach ($n in $Click.Split(',')) {
    if ($n.Trim() -match '^@(\d+)$') { Start-Sleep ([int]$Matches[1]); continue }   # @5 = 多等 5 秒
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n.Trim())
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($el) {
      try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
      catch { try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { } } }
    } else { "找不到按鈕 $n" | Out-File "$env:USERPROFILE\click.log" -Append }
    Start-Sleep 2
  }
}
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$env:USERPROFILE\shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
"alive=$($null -ne $p -and -not $p.HasExited) screen=$($b.Width)x$($b.Height) err=$err exe=$Exe exists=$(Test-Path $Exe)" | Out-File "$env:USERPROFILE\shot.info"
Copy-Item "$env:LOCALAPPDATA\PhoneCentre\log.txt" "$env:USERPROFILE\app.log" -ErrorAction SilentlyContinue
if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
'done' | Out-File "$env:USERPROFILE\shot.done"
