# Ethernet Packet Generator - Build Instructions

## Requirements

1. **.NET 8 SDK (x64)** — https://dotnet.microsoft.com/download/dotnet/8.0
2. **Npcap** (WinPcap compatible) — https://npcap.com/#download
   - Install with "WinPcap API-compatible Mode" enabled

## Build & Run

```powershell
# From the solution root:
dotnet restore
dotnet build -c Release
dotnet run --project EthernetPacketGenerator
```

Or open `EthernetPacketGenerator.sln` in Visual Studio 2022 and press F5.

## Notes

- Run as Administrator if Npcap requires elevated privileges for raw packet send
- Without Npcap installed, the app still opens but Send will fail with an error in the status bar
