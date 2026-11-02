@echo off
setlocal enabledelayedexpansion

set "BASE_DIR=%~dp0"
set "ROOT_DIR=%BASE_DIR%.."
set "PROTOC_EXE=%BASE_DIR%bin\protoc.exe"
set "PROTOS_DIR=%BASE_DIR%protos"
set "OUTPUT_DIR=%BASE_DIR%output"
set "CS_OUTPUT_DIR=%OUTPUT_DIR%\csharp"
set "UNITY_GENERATED_DIR=%ROOT_DIR%\RedDotSystem-main\Assets\Game\Scripts\Generated"
set "SERVER_GENERATED_DIR=%ROOT_DIR%\competeserves\Server\Generated"

if not exist "%PROTOC_EXE%" (
    echo Error: protoc.exe not found: %PROTOC_EXE%
    exit /b 1
)

if not exist "%PROTOS_DIR%" (
    echo Error: proto directory not found: %PROTOS_DIR%
    exit /b 1
)

if not exist "%CS_OUTPUT_DIR%" mkdir "%CS_OUTPUT_DIR%"
if not exist "%UNITY_GENERATED_DIR%" mkdir "%UNITY_GENERATED_DIR%"
if not exist "%SERVER_GENERATED_DIR%" mkdir "%SERVER_GENERATED_DIR%"

set "file_count=0"

echo Starting protobuf compilation...
echo ========================================

for %%f in ("%PROTOS_DIR%\*.proto") do (
    echo Compiling: %%~nxf
    "%PROTOC_EXE%" --proto_path="%PROTOS_DIR%" --csharp_out="%CS_OUTPUT_DIR%" "%%f"

    if !errorlevel! neq 0 (
        echo   C# generation failed.
        exit /b !errorlevel!
    )

    set /a "file_count+=1"
)

if exist "%CS_OUTPUT_DIR%\*.cs" (
    copy /Y "%CS_OUTPUT_DIR%\*.cs" "%UNITY_GENERATED_DIR%\" >nul
    copy /Y "%CS_OUTPUT_DIR%\*.cs" "%SERVER_GENERATED_DIR%\" >nul
)

echo ========================================
echo Compilation completed.
echo Processed %file_count% proto file(s).
echo Shared C# output: %CS_OUTPUT_DIR%
echo Unity generated output: %UNITY_GENERATED_DIR%
echo Server generated output: %SERVER_GENERATED_DIR%
