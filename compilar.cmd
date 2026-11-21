@echo off
rem Compila a ponte (C++, VC10) e o ARCA Identificar (C#, .NET 4.0), no Windows do WMP.
rem Saida em bin\.
rem   compilar.cmd          so o ARCA Identificar (a ponte fica presa no WMP aberto)
rem   compilar.cmd ponte    a ponte tambem (com o WMP fechado)
setlocal
cd /d "%~dp0"
set FW=%windir%\Microsoft.NET\Framework\v4.0.30319
if not exist bin mkdir bin
echo ==== inicio %date% %time% > compilar.log
if /i not "%1"=="ponte" goto identificar
echo ==== ponte >> compilar.log
set PF=%ProgramFiles(x86)%
if "%PF%"=="" set PF=%ProgramFiles%
call "%PF%\Microsoft Visual Studio 10.0\VC\bin\vcvars32.bat" >nul
cl /nologo /W3 /EHsc /O2 /MT /LD /DUNICODE /D_UNICODE /Fobin\ Ponte\ARCAPonte.cpp /link /DEF:Ponte\ARCAPonte.def /OUT:bin\ARCAPonte.dll >> compilar.log 2>&1
echo ponte: codigo %errorlevel% >> compilar.log
:identificar
echo ==== identificar >> compilar.log
rem O mesmo projeto que o Visual Studio 2010 abre: janelas e tema em XAML.
rem O scanner e o do ARCA Central (Identificar\ScannerCentral.cs), do outro lado da rede.
"%FW%\MSBuild.exe" Identificar\ARCAIdentificar.csproj /nologo /v:minimal /p:Configuration=Release /p:Platform=x86 >> compilar.log 2>&1
echo identificar: codigo %errorlevel% >> compilar.log
rem o Tema.xaml solto de antes: agora vai compilado dentro do .exe
del /q bin\Tema.xaml 2>nul
del /q bin\*.obj bin\*.exp bin\*.lib 2>nul
echo fim >> compilar.log
