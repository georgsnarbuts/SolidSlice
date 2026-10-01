@echo off
:: Builds the add-in and registers it with SolidWorks. Prompts for admin rights.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" || goto :fail
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /nologo /codebase "%~dp0bin\SwToBambu.dll" || goto :fail
echo.
echo Installed. Start SolidWorks and look for the "3D Print" tab.
pause
exit /b 0

:fail
echo.
echo Installation failed.
pause
exit /b 1
