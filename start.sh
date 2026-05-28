#!/bin/bash
# PacketLabManager — Linux 시작 스크립트
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="$ROOT/server"

# 내 IP 감지 (169.254.x.x 링크로컬 포함)
MY_IP=$(ip -4 addr show scope global 2>/dev/null | grep -oP '(?<=inet )\d+\.\d+\.\d+\.\d+' | head -1)
[ -z "$MY_IP" ] && MY_IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -z "$MY_IP" ] && MY_IP="localhost"

echo ""
echo "  ============================================="
echo "   PacketLabManager  -  Port 8080"
echo "   Local : http://localhost:8080"
echo "   Remote: http://$MY_IP:8080"
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

node server.js
