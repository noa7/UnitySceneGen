@echo off
cd /d "%~dp0"
netsh http show urlacl url=http://*:5782/ | find "Everyone" >nul 2>&1
if errorlevel 1 powershell -Command "Start-Process cmd -ArgumentList \'/c netsh http add urlacl url=http://*:5782/ user=Everyone\' -Verb RunAs -Wait"
dotnet build -c Release
if errorlevel 1 (pause & exit /b 1)
start "" bin\UnitySceneGen.exe --port 5782
timeout /t 2 /nobreak >nul
start http://localhost:5782/swagger
