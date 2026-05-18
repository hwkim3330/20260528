# 설치 가이드 (Installation Guide)

## 1. 사전 준비

### 1.1 Npcap 설치

SharpPcap이 패킷을 캡처하려면 Npcap 드라이버가 필요합니다.

1. [https://npcap.com](https://npcap.com) 에서 최신 버전 다운로드 (1.79+)
2. 설치 시 **"Install Npcap in WinPcap API-compatible Mode"** 체크
3. 설치 후 재부팅 권장

> 관리자 권한 없이 캡처하려면 "Support raw 802.11 traffic" 미체크 상태로 "Restrict Npcap driver's access to Administrators only" 를 **해제**하세요.

### 1.2 .NET 8 SDK

```
https://dotnet.microsoft.com/download/dotnet/8.0
```

설치 확인:
```powershell
dotnet --version
# 8.x.x 이상이면 OK
```

### 1.3 Node.js 18 LTS

```
https://nodejs.org/en/download
```

설치 확인:
```powershell
node --version   # v18.x.x 이상
npm --version    # 9.x 이상
```

---

## 2. 클론

```bash
git clone https://github.com/hwkim3330/20260518.git
cd 20260518
```

---

## 3. Node.js 서버 설치

```bash
cd server
npm install
```

설치 확인:
```bash
ls node_modules | head
# accepts, cors, express, ws 등이 보이면 OK
```

---

## 4. C# (WPF) 빌드

> Windows 64-bit, .NET 8 SDK 필요

```bash
cd EthernetPacketGenerator
dotnet build -c Release --nologo
```

성공 시 출력:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

결과물:
```
EthernetPacketGenerator/EthernetPacketGenerator/bin/Release/net8.0-windows/
└── EthernetPacketGenerator.exe   ← 실행 파일
```

### NuGet 패키지 오프라인 문제

인터넷이 없는 환경에서 빌드 실패 시 `nuget.config` 를 확인하세요.  
`EthernetPacketGenerator/LocalPackages/` 에 패키지를 수동 복사 후 재시도.

---

## 5. 실행

### 방법 A: 수동 (두 터미널)

**터미널 1:**
```bash
cd server
node server.js
```
출력:
```
[PacketLabManager] Local   : http://localhost:8080
[PacketLabManager] Network : http://172.16.x.x:8080
[PacketLabManager] Worker  : ws://localhost:8080/ws/worker?workerId=local
```

**터미널 2:**
```
EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe
```

### 방법 B: PowerShell 원라인

```powershell
# 기존 프로세스 정리
Get-Process -Name "node","EthernetPacketGenerator" -ErrorAction SilentlyContinue | Stop-Process -Force

# 서버 시작
$serverDir = "$PWD\server"
Start-Process node -ArgumentList "server.js" -WorkingDirectory $serverDir -WindowStyle Minimized

# WPF 시작
Start-Process ".\EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe"

# 브라우저 열기
Start-Sleep 2
Start-Process "http://localhost:8080"
```

---

## 6. 연결 확인

서버와 WPF가 모두 실행된 후 브라우저에서:

```
http://localhost:8080/api/csharp/health
```

응답:
```json
{ "ok": true, "connected": true, "note": "EthernetPacketGenerator WebSocket worker" }
```

`"connected": true` 이면 WPF ↔ Node.js 연결 완료.

---

## 7. 방화벽 설정 (멀티 노드 시)

다른 PC에서 접근하려면 포트 8080을 허용:

```powershell
# 인바운드 8080 허용 (관리자 권한)
New-NetFirewallRule -DisplayName "PacketLabManager 8080" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
```

---

## 8. 포트 충돌 시

기본 포트(8080) 충돌 시 환경변수로 변경:

```powershell
$env:PORT = "9090"
node server.js
```

WPF 내부 API 서버는 포트 18080을 사용합니다. 충돌 시 `LabApiServer.cs` 의 포트 번호를 수정하고 재빌드하세요.

---

## 9. 문제 해결 (Troubleshooting)

| 증상 | 원인 | 해결 |
|------|------|------|
| 캡처가 안됨 | Npcap 미설치 또는 구버전 | Npcap 1.79+ 재설치 |
| `connected: false` | WPF 미실행 또는 포트 충돌 | WPF 실행 후 새로고침 |
| `npm install` 실패 | Node.js 버전 낮음 | Node.js 18+ 재설치 |
| 빌드 오류 NuGet | 인터넷 차단 | LocalPackages 수동 구성 |
| 인터페이스 목록 비어있음 | Npcap WinPcap 호환모드 미체크 | Npcap 재설치 (WinPcap 호환 체크) |
| 웹 탭 전환 안됨 | WS 연결 끊김 | 페이지 새로고침 |
