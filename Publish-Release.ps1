param([string]$Version = '0.1.1')
$ErrorActionPreference = 'Stop'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$stage = Join-Path $artifacts ([guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'flakeui'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'NeonRainOverlay.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$upstream = 'https://github.com/luke-you/tacky-borders'
$download = Join-Path $stage 'borders.zip'
Invoke-WebRequest "$upstream/releases/download/v1.6.0/tacky-borders-v1.6.0.zip" -OutFile $download
if ((Get-FileHash $download).Hash -ne 'EB75AD4B6B22935F6CEB2E12E36C55BCDF907176953EB47EC7038E4B60A48F23') { throw 'Unexpected Tacky Borders archive' }
Expand-Archive $download (Join-Path $stage 'borders')
$exe = Get-ChildItem (Join-Path $stage 'borders') -Recurse -Filter 'tacky-borders.exe'
if (@($exe).Count -ne 1) { throw 'Missing Tacky Borders executable' }
Copy-Item $exe.FullName $publish
Invoke-WebRequest 'https://raw.githubusercontent.com/luke-you/tacky-borders/v1.6.0/LICENSE' -OutFile (Join-Path $publish 'Tacky-Borders-LICENSE.txt')
$zip = Join-Path $artifacts "flakeui-$Version-win-x64.zip"
Compress-Archive -Path $publish -DestinationPath $zip -Force
Write-Output $zip