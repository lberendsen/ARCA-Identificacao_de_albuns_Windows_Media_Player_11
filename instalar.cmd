@echo off
rem Registra a ponte do ARCA no WMP (32 bits). Feche o WMP antes; precisa de administrador.
rem Desfazer: desinstalar.cmd
cd /d "%~dp0"
set RS=%windir%\SysWOW64\regsvr32.exe
if not exist "%RS%" set RS=%windir%\System32\regsvr32.exe
echo ==== %date% %time% > instalar.log
"%RS%" /s "%~dp0bin\ARCAPonte.dll"
echo ponte registrada: codigo %errorlevel% >> instalar.log
reg query "HKLM\SOFTWARE\Wow6432Node\Microsoft\MediaPlayer\UIPlugins" >> instalar.log 2>&1
reg query "HKLM\SOFTWARE\Microsoft\MediaPlayer\UIPlugins" >> instalar.log 2>&1
