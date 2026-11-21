@echo off
rem Testes do ARCA Identificar fora do Visual Studio (no VS 2010: Teste > Executar > Todos os testes da solucao).
rem   rodar-testes.cmd             todos
rem   rodar-testes.cmd Unidade     so os que nao precisam de rede nem de musica
rem   rodar-testes.cmd Integracao  os que usam o ARCA Central, o acervo (so leitura) e as janelas
rem O acervo dos testes: variavel ARCA_TESTE_ACERVO (padrao D:\Musicas). Se houver outros projetos de
rem teste na mesma pasta, eles entram tambem (categorias deles: Placa, WMP).
rem Resultado em Testes\Resultados\*.trx (abre no VS 2010) e o resumo em Testes\Resultados\ultimo.txt.
setlocal
cd /d "%~dp0.."
set FW=%windir%\Microsoft.NET\Framework\v4.0.30319
set PF=%ProgramFiles(x86)%
if "%PF%"=="" set PF=%ProgramFiles%
set MST=%PF%\Microsoft Visual Studio 10.0\Common7\IDE\MSTest.exe
if not exist Testes\Resultados mkdir Testes\Resultados
set SLN=ARCA-Identificacao.sln
if exist ARCA-WMP.sln set SLN=ARCA-WMP.sln
"%FW%\MSBuild.exe" %SLN% /nologo /v:minimal /p:Configuration=Release /p:Platform=x86 > Testes\Resultados\build.txt 2>&1
if errorlevel 1 (echo FALHOU A COMPILACAO - veja Testes\Resultados\build.txt & type Testes\Resultados\build.txt & goto fim)
set FILTRO=
if not "%1"=="" set FILTRO=/category:%1
for /f "tokens=1-3 delims=/ " %%a in ("%date%") do set DIA=%%c%%b%%a
set HORA=%time: =0%
set HORA=%HORA::=%
set HORA=%HORA:,=%
set TRX=Testes\Resultados\testes-%DIA%-%HORA:~0,6%.trx
rem Os projetos de teste que existirem nesta pasta (o do Identificar sempre).
set CONT=/testcontainer:Testes\bin\Release\ARCAIdentificar.Testes.dll
if exist TestesVisual\bin\Release\ARCAVisual.Testes.dll set CONT=%CONT% /testcontainer:TestesVisual\bin\Release\ARCAVisual.Testes.dll
"%MST%" /nologo %CONT% %FILTRO% /resultsfile:%TRX% > Testes\Resultados\ultimo.txt 2>&1
type Testes\Resultados\ultimo.txt
:fim
endlocal
