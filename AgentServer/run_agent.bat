@echo off
rem Agent launcher (Node/TypeScript build) -- switched from the Python version on 2026-10-18.
rem Listens on 127.0.0.1:8898 (= the .NET gateway's default CS_AGENT_PYTHON_URL).
rem NOTE: keep this file ASCII-only and CRLF-terminated; cmd.exe mis-parses LF-only .bat files.
cd /d %~dp0

if not exist node_modules (
    echo [run_agent] node_modules missing. Run: npm install
    pause
    exit /b 1
)

if not exist dist\server.js (
    echo [run_agent] dist not found, running npm run build ...
    call npm run build
    if errorlevel 1 (
        echo [run_agent] build failed, aborting
        pause
        exit /b 1
    )
)

echo [run_agent] starting agent service at http://127.0.0.1:8898  (Ctrl+C to stop)
node dist\server.js