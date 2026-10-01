@echo off
:: Unregisters the add-in from SolidWorks. Prompts for admin rights.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /nologo /unregister "%~dp0bin\SwToBambu.dll"
pause
