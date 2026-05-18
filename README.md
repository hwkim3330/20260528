# PacketLabManager

**이더넷 패킷 생성 · 캡처 · 시나리오 검증 통합 플랫폼**

> Integrated Ethernet Packet Generation, Capture & Scenario Validation Platform

---

## 개요 (Overview)

PacketLabManager는 이더넷 네트워크 테스트를 위한 풀스택 랩 도구입니다.  
WPF 데스크탑 앱, Node.js 관리 서버, 웹 UI 세 계층으로 구성됩니다.

| 구성요소 | 역할 |
|---------|------|
| **EthernetPacketGenerator** (WPF, .NET 8) | 패킷 전송·캡처 엔진 + HyperTerminal (시리얼·레지스터·FDB·자동화) |
| **PacketLabManager Server** (Node.js) | REST API + WebSocket 허브 — 여러 워커 노드 조율 및 웹 UI 서빙 |
| **Web UI** (Vanilla JS) | 브라우저 기반 인터페이스 — WPF 탭과 WebSocket으로 실시간 동기화 |

### 주요 기능

- UDP / TCP / ICMP / ARP / 커스텀 이더넷 패킷 전송 (VLAN 지원)
- SharpPcap 기반 실시간 패킷 캡처 + 헥스·프로토콜 디코드
- 멀티 노드 시나리오 랩 (A↔B 포워딩 테스트, PASS/FAIL 리포트)
- HyperTerminal: 시리얼 콘솔, PHY 레지스터 R/W, FDB 테이블, 자동화 시퀀스
- 테스트 케이스 관리 및 매크로 시퀀서

---

## 아키텍처 (Architecture)

```
┌────────────────────────────────────────────────────────────┐
│  Browser  http://localhost:8080                            │
│  Vanilla JS Web UI  ◄──── WebSocket ────►                 │
└────────────────────┬───────────────────────────────────────┘
                     │  REST / WebSocket
┌────────────────────▼───────────────────────────────────────┐
│  PacketLabManager Server  (Node.js  :8080)                 │
│  Express REST API  +  WorkerHub (WebSocket)                │
│  /api/send  /api/capture  /api/tty  /api/register  ...     │
└────────────────────┬───────────────────────────────────────┘
                     │  ws://localhost:8080/ws/worker
┌────────────────────▼───────────────────────────────────────┐
│  EthernetPacketGenerator.exe  (WPF  .NET 8  Windows x64)  │
│  SharpPcap  ·  SerialPort  ·  Register  ·  FDB             │
│  탭 전환 이벤트 → tabchange → Web UI 자동 동기화            │
└────────────────────────────────────────────────────────────┘
```

자세한 내용: [`docs/architecture.md`](docs/architecture.md)

---

## 사전 요구사항 (Prerequisites)

| 항목 | 버전 | 비고 |
|------|------|------|
| Windows | 10 / 11  64-bit | WPF 앱 실행 필수 |
| [Npcap](https://npcap.com) | 1.79 이상 | 패킷 캡처 드라이버 |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0+ | C# 빌드 시 필요 |
| .NET 8 Runtime | 8.0+ | EXE 실행만 할 경우 |
| [Node.js](https://nodejs.org) | 18 LTS 이상 | 서버 실행 |
| Git | 2.x | 소스 클론 |

> **Npcap 설치 시** "WinPcap API-compatible Mode" 옵션을 체크하세요.

---

## 설치 및 실행 (Installation & Run)

### 1. 클론

```bash
git clone https://github.com/hwkim3330/20260518.git
cd 20260518
```

### 2. Node.js 패키지 설치

```bash
cd server
npm install
```

### 3. C# 빌드

> .NET 8 SDK와 Windows 환경 필요

```bash
cd EthernetPacketGenerator
dotnet build -c Release --nologo
```

빌드 결과물 위치:
```
EthernetPacketGenerator/EthernetPacketGenerator/bin/Release/net8.0-windows/EthernetPacketGenerator.exe
```

### 4. 실행

**터미널 1 — Node.js 서버:**
```bash
cd server
node server.js
```
→ `http://localhost:8080` 에서 웹 UI 접근

**터미널 2 — WPF 앱:**
```
EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe
```

### PowerShell 원라인 실행

```powershell
# 서버 시작
Start-Process node -ArgumentList "server.js" -WorkingDirectory ".\server"
# WPF 앱 시작
Start-Process ".\EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe"
```

브라우저에서 `http://localhost:8080` 열기.

---

## 웹 UI 탭 안내 (Web UI Tabs)

| 탭 | 설명 |
|----|------|
| **Packet Generator** | UDP·TCP·ICMP·ARP·Raw 이더넷 패킷 빌드 및 전송 |
| **Scenario Lab** | 멀티 노드 A↔B 포워딩 시나리오 테스트 (PASS/FAIL 리포트) |
| **Capture** | 실시간 패킷 캡처 — 헥스 덤프 · 프로토콜 계층 디코드 |
| **HyperTerminal** | 시리얼 콘솔·레지스터·FDB·자동화 통합 터미널 |

### HyperTerminal 서브탭

| 서브탭 | 설명 |
|--------|------|
| **Serial** | 전이중 시리얼 콘솔 (HEX / ASCII / 혼합 모드) |
| **Control** | PHY·스위치 컨트롤 패널, 레지스터 페어 뷰 |
| **Register** | 개별 레지스터 읽기·쓰기 |
| **FDB** | Forwarding Database 조회 및 관리 |
| **Auto** | 자동화 시퀀스 실행기 |

---

## API 요약 (API Reference)

Base URL: `http://localhost:8080/api`

| Method | Endpoint | 설명 |
|--------|----------|------|
| GET | `/interfaces` | 네트워크 인터페이스 목록 |
| POST | `/send` | 패킷 전송 |
| POST | `/build` | 패킷 빌드 (전송 없이) |
| POST | `/capture/start` | 캡처 시작 |
| POST | `/capture/stop` | 캡처 중지 |
| GET | `/capture/packets` | 캡처된 패킷 목록 |
| POST | `/capture/clear` | 캡처 버퍼 초기화 |
| GET | `/tty/list` | 시리얼 포트 목록 |
| POST | `/tty/open` | 시리얼 세션 열기 |
| POST | `/tty/send` | 시리얼 데이터 전송 |
| POST | `/tty/close` | 시리얼 세션 닫기 |
| GET | `/register/status` | 레지스터 서비스 상태 |
| POST | `/register/read` | 레지스터 읽기 |
| POST | `/register/write` | 레지스터 쓰기 |
| GET | `/fdb/table` | FDB 테이블 조회 |
| GET | `/scenario/list` | 시나리오 목록 |
| POST | `/scenario/run` | 시나리오 실행 |
| POST | `/tests/run` | 테스트 케이스 실행 |
| GET | `/workers` | 연결된 워커 노드 목록 |
| GET | `/version` | 버전 정보 |
| GET | `/csharp/health` | WPF 워커 연결 상태 |

전체 API: [`docs/api.md`](docs/api.md)

---

## 프로젝트 구조 (Project Structure)

```
20260518/
├── EthernetPacketGenerator/            # C# WPF 솔루션
│   ├── EthernetPacketGenerator.sln
│   ├── EthernetPacketGenerator/        # 메인 WPF 앱
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── MainWindow.xaml / .cs
│   │   ├── Services/                   # 패킷·캡처·시리얼·레지스터·FDB
│   │   ├── ViewModels/                 # MVVM 뷰모델
│   │   ├── Views/                      # XAML 뷰
│   │   ├── Models/                     # 데이터 모델
│   │   └── wwwroot/                    # 내장 API 서버용 웹 에셋
│   └── PacketLabWorker/                # 독립 실행형 워커 프로세스
├── server/                             # Node.js 관리 서버
│   ├── server.js                       # 진입점
│   ├── package.json
│   ├── routes/                         # REST 라우트 모음
│   ├── services/                       # workerHub, csharpApiClient
│   └── public/                         # 웹 UI (index.html, app.js, styles.css)
├── docs/                               # 문서
│   ├── architecture.md
│   ├── api.md
│   └── installation.md
├── .gitignore
└── README.md
```

---

## 개발 가이드 (Development)

### C# 앱 재빌드

```bash
cd EthernetPacketGenerator
dotnet build -c Release --nologo
```

### 웹 UI 수정

`server/public/app.js`, `styles.css` 편집 후 브라우저 새로고침 — 서버 재시작 불필요.

### 새 API 라우트 추가

1. `server/routes/myroute.js` 생성
2. `server/server.js` 에 등록:
   ```js
   app.use('/api', require('./routes/myroute'));
   ```

### WPF 탭 → Web UI 동기화 구조

```
WPF 탭 전환
  └─► LabWorkerService.cs  →  tabchange 이벤트 전송
        └─► Node.js workerHub  →  WebSocket broadcast
              └─► app.js handleWorkerEvent()  →  showView()
```

---

## 라이선스 (License)

MIT License — © 2026 KETI (Korea Electronics Technology Institute)
