@echo off
rem MinePainter release: tag a version and push it; GitHub Actions does the rest
rem (builds the exe, publishes a Release, and the download page picks it up automatically -
rem  see .github\workflows\release.yml and docs\index.html).
rem
rem Messages are plain English on purpose: cmd.exe renders this file with the OEM
rem code page, so non-ASCII text here comes out as garbage (publish.bat is the same).
rem
rem Usage:
rem   release.bat 1.2.0
setlocal
cd /d "%~dp0"

set APPVER=%~1
if "%APPVER%"=="" (
    echo Usage: release.bat ^<version^>    e.g. release.bat 1.2.0
    exit /b 1
)

echo === Checking for uncommitted changes ===
git diff --quiet && git diff --cached --quiet
if errorlevel 1 (
    echo Commit your changes first:
    git status --short
    if not defined CI pause
    exit /b 1
)

echo === Pushing commits ===
git push
if errorlevel 1 (
    echo Push failed - sort that out before releasing.
    if not defined CI pause
    exit /b 1
)

echo === Tagging v%APPVER% ===
git tag -a "v%APPVER%" -m "MinePainter %APPVER%"
if errorlevel 1 (
    echo Tag v%APPVER% already exists. To re-release the same version:
    echo   git tag -d v%APPVER% ^&^& git push origin :v%APPVER%
    if not defined CI pause
    exit /b 1
)

git push origin "v%APPVER%"
if errorlevel 1 (
    echo Could not push the tag.
    if not defined CI pause
    exit /b 1
)

echo.
echo Sent. Build progress:
echo   https://github.com/DragonL0508/minepainter/actions
echo Download page (points at the new version once the build finishes):
echo   https://dragonl0508.github.io/minepainter/
echo.
if not defined CI pause
endlocal
