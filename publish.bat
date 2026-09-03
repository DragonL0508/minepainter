@echo off
rem MinePainter publish script: builds a distributable exe for other users.
rem
rem Usage:
rem   publish.bat              self-contained single exe (~70-90MB, target PC needs nothing installed)
rem   publish.bat fd           framework-dependent (~10MB, target PC needs .NET 8 Desktop Runtime)
rem   publish.bat sc 1.2.0     also set the version (written into exe properties and zip name)
rem
rem ReadyToRun pre-compiles IL to native code: noticeably faster startup (less JIT).
rem Single-file compression is OFF on purpose: decompressing assemblies at startup
rem delayed the splash from ~80ms to ~320ms after launch. The exe is bigger on disk
rem (~135MB vs ~60MB) but the zip you send is about the same size either way.
rem Output: dist\MinePainter-<version>-<suffix>\MinePainter.exe and a .zip next to it
rem
rem CI note: GitHub Actions sets CI=true, and every "pause" below is skipped in that case
rem (a pause on a runner just hangs the job until it times out).
setlocal
cd /d "%~dp0"

set MODE=%~1
set APPVER=%~2
if "%MODE%"=="" set MODE=sc
if "%APPVER%"=="" set APPVER=1.0.0
set RID=win-x64
set PROJ=src\MinePainter.App

if /i "%MODE%"=="fd" (
    set SELF=false
    set SUFFIX=framework-dependent
) else (
    set SELF=true
    set SUFFIX=%RID%
)

rem NOTE: variable names avoid MSBuild property names (OutDir, Version...):
rem MSBuild imports environment variables as properties.
set DEST=%~dp0dist\MinePainter-%APPVER%-%SUFFIX%
set ZIP=%~dp0dist\MinePainter-%APPVER%-%SUFFIX%.zip
rem Publish into a staging dir first: MSBuild global properties leak into the
rem referenced Core project, which dumps loose DLLs next to the bundled exe.
set STAGE=%~dp0dist\.stage

echo === Cleaning old output ===
if exist "%DEST%" rmdir /s /q "%DEST%"
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if exist "%ZIP%" del /q "%ZIP%"

rem The .mpp thumbnail handler is a separate native DLL: Explorer loads it into its own
rem COM surrogate, so it can't be our single-file exe and can't need the .NET runtime.
rem NativeAOT builds it; the app embeds the result and drops it next to the installed exe.
rem NativeAOT links with MSVC, which its targets locate through vswhere - make sure that's on PATH.
set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\Installer
set THUMBS=%~dp0src\MinePainter.Thumbnails
set THUMBSOUT=%THUMBS%\bin\Release\net8.0-windows\win-x64\publish\MinePainterThumbs.dll
set NATIVEDIR=%~dp0src\MinePainter.App\Assets\Native

echo === Building .mpp thumbnail handler (NativeAOT) ===
dotnet publish "%THUMBS%" -c Release -r win-x64 --nologo
if errorlevel 1 (
    echo.
    echo Thumbnail handler build FAILED - fix that, or Explorer previews ship broken.
    if not defined CI pause
    exit /b 1
)
if not exist "%NATIVEDIR%" mkdir "%NATIVEDIR%"
copy /y "%THUMBSOUT%" "%NATIVEDIR%\MinePainterThumbs.dll" >nul
if errorlevel 1 (
    echo Could not find the thumbnail DLL at %THUMBSOUT%
    if not defined CI pause
    exit /b 1
)

echo === Publishing MinePainter %APPVER% (%SUFFIX%) ===
dotnet publish "%PROJ%" -c Release -r %RID% --self-contained %SELF% ^
    "-p:PublishDir=%STAGE%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=false ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=none ^
    -p:Version=%APPVER% ^
    -p:InformationalVersion=%APPVER% ^
    --nologo
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    if not defined CI pause
    exit /b 1
)

rem Keep only the single-file bundle. The host locates its bundle by its own
rem path, so renaming the exe is safe.
mkdir "%DEST%"
copy /y "%STAGE%\MinePainter.App.exe" "%DEST%\MinePainter.exe" >nul
if errorlevel 1 (
    echo Could not find the published exe in %STAGE%
    if not defined CI pause
    exit /b 1
)
rmdir /s /q "%STAGE%"

echo === Creating zip ===
powershell -NoProfile -Command "Compress-Archive -Path '%DEST%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo Zip failed, but the exe is ready in %DEST%
) else (
    echo.
    echo Done! File to send:
    echo   %ZIP%
)
echo   exe: %DEST%\MinePainter.exe
echo.
if not defined CI pause
endlocal
