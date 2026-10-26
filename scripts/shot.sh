#!/bin/bash
# 在 Windows 機台的桌面上執行 exe 並截圖，拉回 Mac。
# 用法：SSHPASS=<密碼> scripts/shot.sh <arm|intel> <exe 的 Windows 路徑> [exe 參數] [等待秒數] [手機位址] [要點的按鈕名稱，逗號分隔]
# 環境變數：ARM_HOST/ARM_USER、INTEL_HOST/INTEL_USER、OUT（輸出資料夾，預設外接硬碟）
set -e
: "${SSHPASS:?請先設定 SSHPASS}"
export SSHPASS LC_ALL=C
T=$1; EXE=$2; ARGS=${3:-}; WAIT=${4:-20}; PHONE=${5:-}; CLICK=${6:-}
case $T in
  arm) H=${ARM_HOST:-10.211.55.10}; U=${ARM_USER:-iambjlu};;
  intel) H=${INTEL_HOST:-192.168.31.68}; U=${INTEL_USER:-Administrator};;
  *) echo "目標要是 arm 或 intel"; exit 1;;
esac
OUT=${OUT:-/Volumes/MX500_APFS/tmp/PhoneCentre-Windows/out}; mkdir -p "$OUT"
ROOT="$(cd "$(dirname "$0")" && pwd)"
SSHO="-o PreferredAuthentications=password -o StrictHostKeyChecking=accept-new"
w() { sshpass -e ssh $SSHO "$U@$H" "$@" 2>&1 | tr -cd '[:print:]\n' | grep -av "quantum\|store now\|upgraded\|WARNING" || true; }
sshpass -e scp $SSHO "$ROOT/windows/shot.ps1" "$U@$H:C:/Users/$U/shot.ps1" >/dev/null 2>&1
# 參數寫進一個小腳本再執行，避開 schtasks /tr 的多層引號
RUN=$(mktemp)
printf '\xef\xbb\xbf' > "$RUN"   # UTF-8 BOM：Windows PowerShell 5.1 才會把中文參數讀對
printf "& 'C:\\Users\\%s\\shot.ps1' -Exe '%s' -Arguments '%s' -Wait %s -Phone '%s' -Click '%s'\r\n" "$U" "$EXE" "$ARGS" "$WAIT" "$PHONE" "$CLICK" >> "$RUN"
sshpass -e scp $SSHO "$RUN" "$U@$H:C:/Users/$U/shot-run.ps1" >/dev/null 2>&1; rm -f "$RUN"
w "del C:\\Users\\$U\\shot.done C:\\Users\\$U\\shot.png C:\\Users\\$U\\app.log 2>nul & schtasks /create /tn shot /sc once /st 23:59 /it /ru $U /f /tr \"powershell -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File C:\\Users\\$U\\shot-run.ps1\" >nul & schtasks /run /tn shot >nul" >/dev/null
for i in $(seq 1 $((WAIT + 40))); do w "if exist C:\\Users\\$U\\shot.done (echo FIN) else (echo WAIT)" | grep -aq FIN && break; sleep 3; done
w "type C:\\Users\\$U\\shot.info & type C:\\Users\\$U\\app.log" | grep -av "^$" | tail -${LINES:-14}
sshpass -e scp $SSHO "$U@$H:C:/Users/$U/shot.png" "$OUT/shot-$T.png" >/dev/null 2>&1
ls -l "$OUT/shot-$T.png"
