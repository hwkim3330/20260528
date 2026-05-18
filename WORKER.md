# Packet Lab Worker

## Topology

Run one manager server on the control PC:

```bat
cd server
npm.cmd start
```

Run one worker on each test PC:

```bat
start_worker.bat http://MANAGER_PC_IP:8080 pc-a
start_worker.bat http://MANAGER_PC_IP:8080 pc-b
```

The worker connects to:

```text
ws://MANAGER_PC_IP:8080/ws/worker?workerId=pc-a
```

## Manager APIs

List connected workers:

```powershell
Invoke-RestMethod http://MANAGER_PC_IP:8080/api/workers
```

Ask a worker for NICs:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://MANAGER_PC_IP:8080/api/workers/pc-a/command `
  -ContentType application/json `
  -Body '{"command":"getInterfaces"}'
```

Start capture on a worker. Empty `interfaces` means all capture devices:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://MANAGER_PC_IP:8080/api/workers/pc-b/command `
  -ContentType application/json `
  -Body '{"command":"startCapture","payload":{"interfaces":[]}}'
```

Send a raw Ethernet frame from a worker:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://MANAGER_PC_IP:8080/api/workers/pc-a/command `
  -ContentType application/json `
  -Body '{"command":"sendHex","payload":{"interface":"Ethernet","hex":"FF FF FF FF FF FF 02 00 00 00 00 01 88 B5 01 02 03 04"}}'
```

Get captured frames by destination MAC:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://MANAGER_PC_IP:8080/api/workers/pc-b/command `
  -ContentType application/json `
  -Body '{"command":"getCaptures","payload":{"dstMac":"FF:FF:FF:FF:FF:FF","limit":50}}'
```

## Commands

- `getInterfaces`
- `startCapture`
- `stopCapture`
- `clearCapture`
- `sendHex`
- `getCaptures`
- `status`
