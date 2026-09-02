@echo off
rem MinePainter launcher: builds (Release) then starts the app.
rem Usage: run.bat [optional image or .mpp file to open]
setlocal
cd /d "%~dp0"

set EXE=src\MinePainter.App\bin\Release\net8.0\MinePainter.App.exe

echo Building MinePainter (Release)...
dotnet build src\MinePainter.App -c Release -v quiet --nologo
if errorlevel 1 (
    if exist "%EXE%" (
        echo Build failed - launching last built version instead.
    ) else (
        echo Build failed and no previous build found.
        pause
        exit /b 1
    )
)

start "" "%EXE%" %1
endlocal
