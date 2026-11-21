# Compila o ARCA Central NO NOTEBOOK (.NET Framework 4.8, que já vem no Windows 11).
# Os clientes de busca e o modelo vêm de ..\Comum (os mesmos que o Identificar usa).
# A saída vai para o disco local (%LOCALAPPDATA%\ARCA-Central), nunca para uma pasta de rede:
# há antivírus que apagam o .exe recém-gerado quando ele é lido pela rede.
$ErrorActionPreference = 'Stop'
$saida  = Join-Path $env:LOCALAPPDATA 'ARCA-Central'
$modelo = Join-Path $PSScriptRoot '..\Comum'
New-Item -ItemType Directory -Force $saida | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fontes = @(
    (Join-Path $PSScriptRoot 'Central.cs'),
    (Join-Path $PSScriptRoot 'Protocolo.cs'),
    (Join-Path $PSScriptRoot 'ScannerRede.cs'),
    (Join-Path $PSScriptRoot 'Discografia.cs'),
    (Join-Path $PSScriptRoot 'BuscaFaixa.cs'),
    (Join-Path $PSScriptRoot 'ConsultaToc.cs'),
    (Join-Path $PSScriptRoot 'BuscaSom.cs'),
    (Join-Path $modelo 'Formato.cs'),
    (Join-Path $modelo 'DiscogsClient.cs'),
    (Join-Path $modelo 'ItunesResolver.cs'),
    (Join-Path $modelo 'ExternalMetadataClient.cs'),
    (Join-Path $modelo 'CandidatoIdentificacao.cs'),
    (Join-Path $modelo 'GnuDbResolver.cs'),   # HigienizarTituloAlbum, usado pelo DiscogsClient
    (Join-Path $modelo 'AccurateRip.cs')      # TTocAudio, de que o GnuDbResolver depende
)
& $csc /nologo /target:exe /optimize+ /codepage:65001 "/out:$saida\ARCACentral.exe" `
    /r:System.Web.Extensions.dll /r:System.Configuration.dll /r:System.Core.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll $fontes
if ($LASTEXITCODE -ne 0) { throw "csc falhou ($LASTEXITCODE)" }
"compilado em $saida\ARCACentral.exe"
