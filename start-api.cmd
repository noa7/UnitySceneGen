@echo off
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Build failed.
    pause
    exit /b %errorlevel%
)
start "" bin\UnitySceneGen.exe --port 5782
timeout /t 2 /nobreak >nul
start http://localhost:5782/swagger
