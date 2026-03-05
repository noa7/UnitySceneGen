@echo off
cd /d "%~dp0"
dotnet build -c Release
if errorlevel 1 (pause & exit /b 1)
bin\UnitySceneGen.exe
