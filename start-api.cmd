@echo off
cd /d "%~dp0"

:: Check if URL is already registered
netsh http show urlacl url=http://*:5782/ | find "Everyone" >nul 2>&1
if %errorlevel% neq 0 (
    echo [API] Registering URL reservation for remote access (requires admin once)...
    :: Re-launch this script as admin to run netsh, then exit the elevated copy
    powershell -Command "Start-Process cmd -ArgumentList '/c netsh http add urlacl url=http://*:5782/ user=Everyone' -Verb RunAs -Wait"
)

dotnet build -c Release
if %errorlevel% neq 0 (
    echo Build failed.
    pause
    exit /b %errorlevel%
)
start "" bin\UnitySceneGen.exe --port 5782
timeout /t 2 /nobreak >nul
start http://localhost:5782/swagger
