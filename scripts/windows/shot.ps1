# 在 Windows 的桌面會話裡啟動 PhoneCentre、等畫面出來、截全螢幕。由 scripts/shot.sh 以互動式排程工作執行。
param([string]$Exe, [string]$Arguments = '', [int]$Wait = 20, [string]$Phone = '')
$adb = "$env:LOCALAPPDATA\PhoneCentre\platform-tools\adb.exe"
if ($Phone) { & $adb connect $Phone *> $null }
Get-Process PhoneCentre -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\PhoneCentre\log.txt", "$env:USERPROFILE\shot.done" -ErrorAction SilentlyContinue
$err = ''
try { $p = if ($Arguments) { Start-Process $Exe -ArgumentList $Arguments -PassThru -ErrorAction Stop } else { Start-Process $Exe -PassThru -ErrorAction Stop } }
catch { $err = $_.Exception.Message; $p = $null }
Start-Sleep $Wait
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save("$env:USERPROFILE\shot.png", [System.Drawing.Imaging.ImageFormat]::Png)
"alive=$($null -ne $p -and -not $p.HasExited) screen=$($b.Width)x$($b.Height) err=$err exe=$Exe exists=$(Test-Path $Exe)" | Out-File "$env:USERPROFILE\shot.info"
Copy-Item "$env:LOCALAPPDATA\PhoneCentre\log.txt" "$env:USERPROFILE\app.log" -ErrorAction SilentlyContinue
if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
'done' | Out-File "$env:USERPROFILE\shot.done"
