@echo off
setlocal
cd /d "%~dp0"
call build.bat
if errorlevel 1 exit /b 1
echo.
echo [selftest]
start "" /b /wait out\HaiDouAssistant.exe --selftest >selftest-output.txt 2>selftest-error.txt
set TEST_EXIT=%ERRORLEVEL%
chcp 65001 >nul
type selftest-output.txt
type selftest-error.txt
echo exit=%TEST_EXIT%
exit /b %TEST_EXIT%
