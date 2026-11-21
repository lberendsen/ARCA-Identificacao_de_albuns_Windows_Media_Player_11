@echo off
rem Tira a ponte do ARCA do WMP. Feche o WMP antes. Nada mais e alterado.
cd /d "%~dp0"
set RS=%windir%\SysWOW64\regsvr32.exe
if not exist "%RS%" set RS=%windir%\System32\regsvr32.exe
"%RS%" /u /s "%~dp0bin\ARCAPonte.dll"
echo desinstalar: codigo %errorlevel% > desinstalar.log
