@echo off
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found
  exit /b 1
)

echo [1/2] compile...
"%CSC%" -nologo -optimize+ -target:winexe -out:HaiDouAssistant.exe -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Management.dll -r:System.Web.Extensions.dll -r:lib\Microsoft.Web.WebView2.Core.dll -r:lib\Microsoft.Web.WebView2.WinForms.dll src\App.cs src\LcuClient.cs src\Maps.cs src\HexData.cs src\HexWindow.cs src\Engine.cs src\UI.cs src\Debug.cs src\SelfTest.cs src\HexTests.cs src\Program.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo [2/2] copy runtime files...
if not exist out mkdir out
copy /y HaiDouAssistant.exe out\ >nul
copy /y lib\*.dll out\ >nul
if exist data xcopy /e /i /y data out\data >nul
if exist wwwroot xcopy /e /i /y wwwroot out\wwwroot >nul
if exist app.ico copy /y app.ico out\ >nul

echo.
echo OK: out\HaiDouAssistant.exe
