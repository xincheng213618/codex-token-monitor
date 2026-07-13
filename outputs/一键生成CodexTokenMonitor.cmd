@echo off
setlocal
chcp 65001 >nul

set "NO_PAUSE="
set "NO_OPEN="
for %%A in (%*) do (
    if /I "%%~A"=="--no-pause" set "NO_PAUSE=1"
    if /I "%%~A"=="--no-open" set "NO_OPEN=1"
)

set "OUTPUT_ROOT=%~dp0"
for %%I in ("%OUTPUT_ROOT%..") do set "PROJECT_ROOT=%%~fI"
set "PROJECT=%PROJECT_ROOT%\src\CodexTokenMonitor.Wpf\CodexTokenMonitor.Wpf.csproj"
set "PUBLISH_DIR=%OUTPUT_ROOT%CodexTokenMonitor"
set "EXE=%PUBLISH_DIR%\CodexTokenMonitor.exe"

title Build CodexTokenMonitor
echo.
echo ========================================
echo   Building CodexTokenMonitor (Release)
echo ========================================
echo.

if not exist "%PROJECT%" (
    echo [ERROR] Project file was not found:
    echo         %PROJECT%
    goto :failed
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK was not found. Install .NET 8 SDK first.
    goto :failed
)

pushd "%PROJECT_ROOT%"
dotnet publish "%PROJECT%" -c Release -o "%PUBLISH_DIR%" --nologo
set "BUILD_RESULT=%ERRORLEVEL%"
popd

if not "%BUILD_RESULT%"=="0" goto :failed
if not exist "%EXE%" (
    echo [ERROR] Build finished, but the exe was not generated:
    echo         %EXE%
    goto :failed
)

echo.
echo [SUCCESS] Generated:
echo           %EXE%
echo.

if not defined NO_OPEN start "" explorer.exe /select,"%EXE%"
call :wait_if_needed
exit /b 0

:failed
echo.
echo [FAILED] CodexTokenMonitor was not generated.
echo.
call :wait_if_needed
exit /b 1

:wait_if_needed
if not defined NO_PAUSE pause
exit /b 0
