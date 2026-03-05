@echo off
start "" "UnitySceneGen.exe" --port 5782
timeout /t 2 /nobreak >nul
start http://localhost:5782/swagger
