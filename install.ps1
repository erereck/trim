# Builds (if needed) and installs Trim for the current user, silently, through the real installer.
# Uninstall: Settings > Apps > Trim
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'bin\TrimSetup.exe'
if (-not (Test-Path $setup)) { & (Join-Path $PSScriptRoot 'build.ps1') | Out-Null }
$p = Start-Process $setup -ArgumentList '/S' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "falha na instalação (código $($p.ExitCode))" }
"Instalado em $env:LOCALAPPDATA\Programs\Trim"
