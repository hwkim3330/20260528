#!/bin/bash
# PacketLabManager — Linux 시작 스크립트 (자동 재시작 포함)
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="$ROOT/server"

# 내 IP 감지 (169.254.x.x 링크로컬 포함)
MY_IP=$(ip -4 addr show scope global 2>/dev/null | grep -oP '(?<=inet )\d+\.\d+\.\d+\.\d+' | head -1)
MY_LINK=$(ip -4 addr show scope link 2>/dev/null | grep -oP '(?<=inet )\d+\.\d+\.\d+\.\d+' | grep -v '^127\.' | head -1)
[ -z "$MY_IP" ] && MY_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -z "$MY_IP" ] && MY_IP="localhost"

echo ""
echo "  ============================================="
echo "   PacketLabManager  -  Port 8080"
echo "   Local     : http://localhost:8080"
[ -n "$MY_IP" ]   && echo "   Network   : http://$MY_IP:8080"
[ -n "$MY_LINK" ] && echo "   Link-Local: http://$MY_LINK:8080"
echo "  ============================================="
echo ""

cd "$SERVER_DIR"

# npm install (node_modules 없을 때)
if [ ! -d node_modules ]; then
    echo "[1/2] npm install..."
    npm install
fi

echo "[2/2] Starting server (requires root for packet capture/send)..."
echo ""

if [ "$(id -u)" -ne 0 ]; then
    echo "  [WARN] root 아님 — 패킷 전송/캡처에 root 권한 필요"
    echo "  sudo ./start.sh  또는  sudo node server/server.js"
    echo ""
fi

# 자동 재시작: cap 네이티브 모듈 crash(exit 1) 시 즉시 재시작
RESTART_DELAY=1
while true; do
    node server.js
    EXIT_CODE=$?
    if [ $EXIT_CODE -eq 0 ]; then
        # 정상 종료 (SIGINT 등) — 재시작 안 함
        echo ""
        echo "  [PacketLabManager] 서버 종료."
        break
    else
        echo ""
        echo "  [PacketLabManager] 비정상 종료 (exit $EXIT_CODE) — ${RESTART_DELAY}초 후 재시작..."
        sleep $RESTART_DELAY
    fi
done
