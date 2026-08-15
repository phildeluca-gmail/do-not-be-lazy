@echo off
:: ============================================================
:: git-commit-generic.bat
:: Drop this file in the root of any git repository.
:: Customize the STAGE section below for your project,
:: then double-click to stage, commit, and push.
:: ============================================================

cd /d "%~dp0"

:: ============================================================
:: STAGE — replace or add "git add" lines for your project
:: ============================================================
git add "SpotTheDifference Files/"
:: git add "src/"
:: git add "assets/"
:: git add "*.json"
:: git add -A     <- stages everything (use with caution)

:: Show what will be committed
git status

:: Prompt for commit message
set /p MSG="Commit message: "
if "%MSG%"=="" set MSG=Update files

git commit -m "%MSG%"
git push

echo.
echo Done.
pause
