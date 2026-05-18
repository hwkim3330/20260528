@echo off
chcp 65001 >nul
title Packet Lab Manager

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "ROOT=%~dp0"
set "SERVER_DIR=%ROOT%server"

set "WPF_EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\x64\Release\net8.0-windows\EthernetPacketGenerator.exe"
if not exist "%WPF_EXE%" set "WPF_EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\x64\Debug\net8.0-windows\EthernetPacketGenerator.exe"
if not exist "%WPF_EXE%" set "WPF_EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\Release\net8.0-windows\EthernetPacketGenerator.exe"
if not exist "%WPF_EXE%" set "WPF_EXE=%ROOT%EthernetPacketGenerator\EthernetPacketGenerator\bin\Debug\net8.0-windows\EthernetPacketGenerator.exe"

for /f "tokens=*" %%A in ('powershell -NoProfile -Command "$a=Get-NetIPAddress -AddressFamily IPv4|Where-Object{$_.IPAddress -notmatch '^(127\.|169\.254\.)'} |Sort-Object{if($_.IPAddress -match '^(172\.|10\.|192\.168\.)'){0}else{1}}|Select-Object -First 1 -ExpandProperty IPAddress;if($a){$a}else{'localhost'}"') do set "MY_IP=%%A"
if not defined MY_IP set "MY_IP=localhost"

echo.
echo  =====================================================
echo   Packet Lab Manager  -  Port 8080
echo   Local : http://localhost:8080
echo   Remote: http://%MY_IP%:8080
echo  =====================================================
echo.

echo [1/3] Killing old processes...
taskkill /IM EthernetPacketGenerator.exe /F >nul 2>&1
taskkill /IM node.exe /F >nul 2>&1
timeout /t 2 >nul

echo [2/3] Starting Node.js (port 8080)...
if not exist "%SERVER_DIR%\node_modules" (
    pushd "%SERVER_DIR%"
    call npm.cmd install --prefer-offline
    popd
)
start "Node-Server" /MIN cmd /c "pushd %SERVER_DIR% && node server.js"
timeout /t 3 >nul

echo [3/3] Starting EthernetPacketGenerator...
if exist "%WPF_EXE%" (
    start "EthernetPacketGenerator" "%WPF_EXE%" --server ws://127.0.0.1:8080 --worker-id local
    timeout /t 2 >nul
) else (
    echo [WARN] EthernetPacketGenerator.exe not found
)

start http://localhost:8080

echo.
echo  =====================================================
echo   Ready!
echo   Local : http://localhost:8080
echo   Remote: http://%MY_IP%:8080
echo  =====================================================
echo.
pause
