@echo off
rem MinePainter publish script: builds a distributable exe for other users.
rem
rem Usage:
rem   publish.bat              self-contained single exe (~70-90MB, target PC needs nothing installed)
rem   publish.bat fd           framework-dependent (~10MB, target PC needs .NET 8 Desktop Runtime)
rem   publish.bat sc 1.2.0     also set the version (written into exe properties and zip name)
rem
rem Output: dist\MinePainter-<version>-<suffix>\MinePainter.exe and a .zip next to it
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
    rem single-file compression is only supported for self-contained builds
    set COMPRESS=false
) else (
    set SELF=true
    set SUFFIX=%RID%
    set COMPRESS=true
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

echo === Publishing MinePainter %APPVER% (%SUFFIX%) ===
dotnet publish "%PROJ%" -c Release -r %RID% --self-contained %SELF% ^
    "-p:PublishDir=%STAGE%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=%COMPRESS% ^
    -p:DebugType=none ^
    -p:Version=%APPVER% ^
    -p:InformationalVersion=%APPVER% ^
    --nologo
if errorlevel 1 (
    echo.
    echo Publish FAILED.
    pause
    exit /b 1
)

rem Keep only the single-file bundle. The host locates its bundle by its own
rem path, so renaming the exe is safe.
mkdir "%DEST%"
copy /y "%STAGE%\MinePainter.App.exe" "%DEST%\MinePainter.exe" >nul
if errorlevel 1 (
    echo Could not find the published exe in %STAGE%
    pause
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
pause
endlocal
