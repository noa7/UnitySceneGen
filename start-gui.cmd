@echo off
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Build failed.
    pause
    exit /b %errorlevel%
)
bin\UnitySceneGen.exe
