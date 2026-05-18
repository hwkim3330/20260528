# Architecture

## 구성 요소 상세 (Component Details)

### 1. EthernetPacketGenerator (WPF, .NET 8)

Windows x64 전용 데스크탑 앱. SharpPcap을 통해 NIC에 직접 접근하여 패킷을 전송·캡처합니다.

**주요 서비스**

| 서비스 | 파일 | 역할 |
|--------|------|------|
| PacketSendService | `Services/PacketSendService.cs` | 이더넷 프레임 빌드 및 SharpPcap 전송 |
| PacketCaptureService | `Services/PacketCaptureService.cs` | 인터페이스 캡처 + 링 버퍼 관리 |
| LabWorkerService | `Services/LabWorkerService.cs` | Node.js WebSocket 워커 프로토콜 처리 |
| SerialPortService | `Services/SerialPortService.cs` | COM 포트 읽기/쓰기 |
| RegisterService | `Services/RegisterService.cs` | PHY 레지스터 R/W (MDIO over serial) |
| FdbService | `Services/FdbService.cs` | FDB 테이블 조회 |
| LabApiServer | `Services/LabApiServer.cs` | 내부 HTTP API 서버 (포트 18080) |

**MVVM 구조**

```
MainViewModel
├── SendViewModel         (Packet Generator 탭)
├── CaptureViewModel      (Capture 탭)
├── HyperTerminalViewModel (HyperTerminal 탭)
│   ├── SerialPortService
│   ├── RegisterService
│   └── FdbService
├── AutomationViewModel   (Auto 서브탭)
└── TestCaseManagerViewModel
```

**탭 동기화 흐름**

```csharp
// LabWorkerService.cs
MainVm.PropertyChanged += (s, e) => {
    if (e.PropertyName == nameof(MainVm.SelectedTabIndex))
        SendTabChangeEvent(_tabIndexToView[MainVm.SelectedTabIndex]);
};
```

탭 인덱스 → view 이름 매핑:

| 인덱스 | View 이름 |
|--------|-----------|
| 0 | senderView |
| 1 | labView |
| 2 | captureView |
| 3 | captureView |
| 4 | serialView (HyperTerminal) |

---

### 2. PacketLabManager Server (Node.js)

Express 4 + ws 8 기반 관리 서버. 포트 8080.

**WorkerHub**

`services/workerHub.js` 는 WPF 인스턴스(워커)와 브라우저를 연결하는 WebSocket 허브입니다.

```
Browser ──WS──► server (port 8080)
                  │
                  ├── WorkerHub.attach(wss)
                  │     WPF Worker ──WS──► ws://localhost:8080/ws/worker?workerId=local
                  │
                  └── broadcast() → 브라우저로 이벤트 전파
```

워커 프로토콜 (JSON over WebSocket):

```json
// 서버 → 워커 명령
{ "id": "uuid", "command": "send", "payload": { ... } }

// 워커 → 서버 응답
{ "id": "uuid", "ok": true, "data": { ... } }

// 워커 → 서버 이벤트 (비동기)
{ "kind": "tabchange", "view": "serialView" }
```

**라우트 구조**

```
server/routes/
├── packet.js       GET /interfaces, POST /send, POST /build
├── capture.js      POST /capture/start|stop|clear, GET /capture/packets
├── tty.js          GET /tty/list, POST /tty/open|send|close
├── register.js     GET /register/status, POST /register/read|write
├── fdb.js          GET /fdb/table, POST /fdb/flush
├── serial.js       시리얼 설정 관련
├── scenario.js     시나리오 실행 (C# 프록시)
├── testcases.js    테스트 케이스 CRUD
├── tests.js        테스트 실행
├── macro.js        매크로 시퀀스
├── packetFlow.js   패킷 플로우 모니터
├── workers.js      워커 노드 목록
├── logs.js         로그 파일 조회
└── health.js       헬스체크
```

---

### 3. Web UI (Vanilla JS)

`server/public/` 의 단일 페이지 앱. 빌드 도구 없이 순수 JavaScript.

**탭 구조**

```
#app
├── .topbar         (로고, 연결 상태)
├── .toolbar        (네트워크 인터페이스 선택, 노드 링크)
├── .linkStrip      (멀티 노드 링크 바 — serialMode 시 숨김)
└── .workspace      (현재 활성 roleView)
    ├── #senderView.roleView      (Packet Generator)
    ├── #labView.roleView         (Scenario Lab)
    ├── #captureView.roleView     (Capture)
    └── #hyperTerminalView.roleView.htView  (HyperTerminal)
        ├── .htSubTabBar
        │   ├── [data-htview="serialView"]
        │   ├── [data-htview="controlView"]
        │   ├── [data-htview="registerView"]
        │   ├── [data-htview="fdbView"]
        │   └── [data-htview="autoView"]
        ├── #serialView.htSubView
        ├── #controlView.htSubView
        ├── #registerView.htSubView
        ├── #fdbView.htSubView
        └── #autoView.htSubView
```

**WebSocket 이벤트 처리**

```javascript
// WPF에서 탭 전환 시 웹도 자동 전환
function handleWorkerEvent(payload) {
    if (payload.kind === 'tabchange' && payload.view) {
        showView(payload.view);
    }
}
```

---

## CSS 레이아웃 구조

```
body (overflow: hidden)
└── .app (display: grid; grid-template-rows: auto auto auto 1fr)
    ├── .topbar        (row 1, auto)
    ├── .toolbar       (row 2, auto)
    ├── .linkStrip     (row 3, auto — serialMode 시 display:none)
    └── .workspace     (row 4, 1fr — grid-row:4 고정)
        └── .roleView.active (height:100%, display:grid)
            ├── .sidebar   (overflow:auto, max-height:calc)
            └── .workPane  (overflow-y:auto)
```

`#hyperTerminalView.htView.active`는 `display:flex; flex-direction:column`으로 오버라이드:

```
hyperTerminalView (flex column, height:100%)
├── .htSubTabBar   (flex-shrink:0)
└── .htSubView.active (flex:1, overflow-y:auto)
```
