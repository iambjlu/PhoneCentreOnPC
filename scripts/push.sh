#!/bin/bash
# 把 deploy.sh 建好的 x64 exe 放到兩台 Windows 的桌面資料夾 PhoneCentre-Windows\x64。
# 用法：SSHPASS=<密碼> scripts/push.sh   （兩台密碼相同時）
set -e
: "${SSHPASS:?請先設定 SSHPASS}"
export SSHPASS LC_ALL=C
WORK=${WORK:-/Volumes/MX500_APFS/tmp/PhoneCentre-Windows/build}
SSHO="-o PreferredAuthentications=password -o StrictHostKeyChecking=accept-new"
sshpass -e scp $SSHO "iambjlu@10.211.55.10:C:/Users/iambjlu/src/publish/x64/PhoneCentre.exe" "$WORK/PhoneCentre.exe" >/dev/null 2>&1
sshpass -e scp $SSHO "$WORK/PhoneCentre.exe" "iambjlu@10.211.55.10:C:/Users/iambjlu/Desktop/PhoneCentre-Windows/x64/PhoneCentre.exe" >/dev/null 2>&1
sshpass -e scp $SSHO "$WORK/PhoneCentre.exe" "Administrator@192.168.31.68:C:/Users/Administrator/Desktop/PhoneCentre-Windows/x64/PhoneCentre.exe" >/dev/null 2>&1
ls -l "$WORK/PhoneCentre.exe"
