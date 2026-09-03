@echo off
rem MinePainter launcher: builds (Release) then starts the app.
rem Usage: run.bat [optional image or .mpp file to open]
setlocal
cd /d "%~dp0"

set EXE=src\MinePainter.App\bin\Release\net8.0\MinePainter.App.exe

rem YouTube preview thumbnails: convert anything dropped in
rem src\MinePainter.App\Assets\YouTubePreview\_source into the 480x270 .webp
rem files that get embedded. No-op when nothing changed; never fails the build.
echo Packing YouTube preview thumbnails...
dotnet run --project tools\ThumbPack --verbosity quiet --nologo

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
