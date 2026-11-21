@echo off
rem Registra a ponte do ARCA no WMP (32 bits). Feche o WMP antes; precisa de administrador.
rem Desfazer: desinstalar.cmd
cd /d "%~dp0"
set RS=%windir%\SysWOW64\regsvr32.exe
if not exist "%RS%" set RS=%windir%\System32\regsvr32.exe
echo ==== %date% %time% > instalar.log
"%RS%" /s "%~dp0bin\ARCAPonte.dll"
echo ponte registrada: codigo %errorlevel% >> instalar.log
rem Se o WMP tiver desligado a ponte (ele faz isso sozinho depois de fechar travado), apagar a
rem marca de autorun faz o WMP liga-la de novo na proxima vez que abrir. Rode como o seu usuario.
reg delete "HKCU\Software\Microsoft\MediaPlayer\UIPlugins\{EF03AEB5-0BA5-4ACE-9A5F-BDBF1FF99321}" /v AttemptedAutoRun /f >> instalar.log 2>&1
reg query "HKLM\SOFTWARE\Wow6432Node\Microsoft\MediaPlayer\UIPlugins" >> instalar.log 2>&1
reg query "HKLM\SOFTWARE\Microsoft\MediaPlayer\UIPlugins" >> instalar.log 2>&1
