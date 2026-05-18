@echo off
title Packet Lab Worker

set "ROOT=%~dp0"
set "WORKER=%ROOT%EthernetPacketGenerator\PacketLabWorker\bin\x64\Release\net8.0-windows\PacketLabWorker.exe"
set "SERVER=%~1"
set "WORKER_ID=%~2"

if "%SERVER%"=="" set "SERVER=http://127.0.0.1:8080"
if "%WORKER_ID%"=="" set "WORKER_ID=%COMPUTERNAME%"

if not exist "%WORKER%" (
    echo [ERROR] PacketLabWorker.exe not found.
    echo Build it first:
    echo   dotnet build "%ROOT%EthernetPacketGenerator\PacketLabWorker\PacketLabWorker.csproj" -c Release -p:Platform=x64
    pause
    exit /b 1
)

echo Packet Lab Worker
echo Server:    %SERVER%
echo Worker ID: %WORKER_ID%
echo.
"%WORKER%" --server "%SERVER%" --worker-id "%WORKER_ID%"
