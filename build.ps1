# Builds Trim.exe and TrimSetup.exe with the C# compiler that ships with Windows (.NET Framework 4.8) — no SDK needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc  = "$fw\csc.exe"
$wpf  = "$fw\WPF"
New-Item -ItemType Directory -Force "$root\bin", "$root\obj" | Out-Null

# compiler arguments go through a response file so paths with spaces are quoted correctly
function Compile([string]$name, [string[]]$lines) {
    $rsp = "$root\obj\$name.rsp"
    [IO.File]::WriteAllLines($rsp, $lines, (New-Object Text.UTF8Encoding $false))
    $out = & $csc "@$rsp" 2>&1
    if ($LASTEXITCODE) { $out | ForEach-Object { "$_" }; throw "$name build failed" }
}

$refs = @(
    "/r:`"$wpf\PresentationCore.dll`"", "/r:`"$wpf\PresentationFramework.dll`"", "/r:`"$wpf\WindowsBase.dll`"",
    "/r:`"$fw\System.Xaml.dll`"", "/r:System.dll", "/r:System.Core.dll"
)
$common = @("/nologo", "/target:winexe", "/optimize+", "/debug-", "/platform:anycpu", "/utf8output",
            "/win32icon:`"$root\assets\trim.ico`"", "/win32manifest:`"$root\assets\app.manifest`"")

if (-not (Test-Path "$root\assets\trim.ico")) {
    Compile "MakeIcon" @("/nologo", "/target:exe", "/out:`"$root\obj\MakeIcon.exe`"", "/r:System.Drawing.dll", "`"$root\tools\MakeIcon.cs`"")
    & "$root\obj\MakeIcon.exe" "$root\assets\trim.ico" "$root\assets\trim-256.png"
}

# the app
$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { "`"$($_.FullName)`"" }
Compile "Trim" ($common + $refs + @("/out:`"$root\bin\Trim.exe`"") + $src)

# the installer: one exe with Trim.exe and the icon embedded
Compile "TrimSetup" ($common + $refs + @(
    "/out:`"$root\bin\TrimSetup.exe`"",
    "/resource:`"$root\bin\Trim.exe`",Trim.exe",
    "/resource:`"$root\assets\trim-256.png`",icon.png",
    "`"$root\setup\Setup.cs`""))

Get-Item "$root\bin\Trim.exe", "$root\bin\TrimSetup.exe" | ForEach-Object { "{0}  {1:N0} KB" -f $_.Name, ($_.Length / 1KB) }
