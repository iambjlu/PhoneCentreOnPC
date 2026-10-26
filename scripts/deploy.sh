#!/bin/bash
# 把專案同步到「建置機」（Windows on ARM）並發行單一 exe。
# 用法：SSHPASS=<密碼> scripts/deploy.sh [x64|arm64] [輸出行數]
# 環境變數：ARM_HOST（預設 10.211.55.10）、ARM_USER（預設 iambjlu）、WORK（Mac 端暫存，預設外接硬碟）
set -e
: "${SSHPASS:?請先設定 SSHPASS（建置機的密碼）}"
export SSHPASS LC_ALL=C COPYFILE_DISABLE=1
ARM_HOST=${ARM_HOST:-10.211.55.10}; ARM_USER=${ARM_USER:-iambjlu}
WORK=${WORK:-/Volumes/MX500_APFS/tmp/PhoneCentre-Windows/build}
P=${1:-x64}; LINES=${2:-25}
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$WORK"
SSHO="-o PreferredAuthentications=password -o StrictHostKeyChecking=accept-new"
arm() { sshpass -e ssh $SSHO "$ARM_USER@$ARM_HOST" "$@" 2>&1 | tr -cd '[:print:]\n' | grep -av "quantum\|store now\|upgraded\|WARNING" || true; }

tar -C "$ROOT" --exclude=bin --exclude=obj --exclude=publish --exclude=.git --exclude='._*' -cf "$WORK/win.tar" .
sshpass -e scp $SSHO "$WORK/win.tar" "$ARM_USER@$ARM_HOST:C:/Users/$ARM_USER/win.tar" >/dev/null 2>&1

cat > "$WORK/build.ps1" <<PS
Set-Location C:\\Users\\$ARM_USER
Remove-Item -Recurse -Force src -ErrorAction SilentlyContinue
New-Item -ItemType Directory src | Out-Null
tar -xf win.tar -C src
Set-Location src
& 'C:\\Program Files\\dotnet\\dotnet.exe' publish PhoneCentreOnPC.csproj -c Release -r win-$P -p:Platform=$P -o publish\\$P *> C:\\Users\\$ARM_USER\\build.log
'done' | Out-File C:\\Users\\$ARM_USER\\build.done
PS
sshpass -e scp $SSHO "$WORK/build.ps1" "$ARM_USER@$ARM_HOST:C:/Users/$ARM_USER/build.ps1" >/dev/null 2>&1
arm "del C:\\Users\\$ARM_USER\\build.done 2>nul & schtasks /create /tn bld /sc once /st 23:59 /rl highest /f /tr \"powershell -NoProfile -ExecutionPolicy Bypass -File C:\\Users\\$ARM_USER\\build.ps1\" >nul & schtasks /run /tn bld >nul" >/dev/null
for i in $(seq 1 80); do
  r=$(arm "if exist C:\\Users\\$ARM_USER\\build.done (echo FIN) else (echo WAIT)" | grep -aE "FIN|WAIT" | tail -1)
  [ "$r" = "FIN" ] && break; sleep 10
done
echo "status: $r"
arm "type C:\\Users\\$ARM_USER\\build.log | findstr /i \"error warning Elapsed ->\" & dir C:\\Users\\$ARM_USER\\src\\publish\\$P" | tail -"$LINES"
