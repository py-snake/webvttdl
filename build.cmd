@echo off
setlocal

echo ==========================================
echo webvttdl Build (Windows)
echo ==========================================

set CONFIG=Release
set PLATFORM=x86

if not "%1"=="" set CONFIG=%1

echo Configuration: %CONFIG%
echo.

cd /d "%~dp0webvttdl"

if exist "bin\%CONFIG%" rmdir /s /q "bin\%CONFIG%"
if exist "obj" rmdir /s /q "obj"

echo Building...
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" webvttdl.csproj /p:Configuration=%CONFIG% /p:Platform=%PLATFORM% /verbosity:minimal

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build FAILED!
    pause
    exit /b 1
)

echo.
echo Creating app.config...
(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<configuration^>
echo   ^<startup^>
echo     ^<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.0"/^>
echo   ^</startup^>
echo ^</configuration^>
) > "bin\%CONFIG%\webvttdl.exe.config"

if exist "bin\curl" (
    echo Copying curl files...
    copy /Y "bin\curl\*" "bin\%CONFIG%\" > nul
) else (
    echo WARNING: bin\curl folder not found, skipping curl copy.
)

echo Build successful!
echo Output: %~dp0webvttdl\bin\%CONFIG%\
echo.
echo Usage: webvttdl.exe ^<master-m3u8-url^>
pause
