@echo off
setlocal

rem window title: make it clear this console IS the game server
title CompeteServer - close window = stop server

rem ==============================================================
rem CompeteServer launcher (TEMPLATE - safe to commit)
rem
rem Copy this file to run_server.bat and fill in your local MySQL
rem credentials there. run_server.bat is git-ignored on purpose.
rem
rem DB config via environment variables:
rem   CS_DB_HOST       default 127.0.0.1
rem   CS_DB_PORT       default 3306
rem   CS_DB_NAME       default compete
rem   CS_DB_USER       REQUIRED
rem   CS_DB_PASSWORD   REQUIRED
rem ==============================================================

set "CS_DB_USER=YOUR_MYSQL_USER"
set "CS_DB_PASSWORD=YOUR_MYSQL_PASSWORD"

set "CS_DB_HOST=127.0.0.1"
set "CS_DB_PORT=3306"
set "CS_DB_NAME=compete"

cd /d "%~dp0Server"
dotnet run
pause
