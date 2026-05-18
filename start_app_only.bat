@echo off
title Ethernet Packet Generator

set "ROOT=%~dp0"
set "EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\x64\Release\net8.0-windows\EthernetPacketGenerator.exe"
if not exist "%EXE%" (
    set "EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\x64\Debug\net8.0-windows\EthernetPacketGenerator.exe"
)
if not exist "%EXE%" (
    set "EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe"
)
if not exist "%EXE%" (
    set "EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\Debug\net8.0-windows\EthernetPacketGenerator.exe"
)

if not exist "%EXE%" (
    echo [ERROR] EthernetPacketGenerator.exe not found.
    echo Build the project first: dotnet build -c Release
    pause
    exit /b 1
)

start "" "%EXE%"
