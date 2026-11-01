# fuseraft-web build (Windows)
#
#   .\build.ps1                        build + test
#   .\build.ps1 publish                framework-dependent publish to artifacts\publish\portable\
#   .\build.ps1 publish win-x64        self-contained publish for one platform
#   .\build.ps1 pack win-x64           publish, then archive to artifacts\fuseraft-web-<version>-<rid>.zip
#
# Set $env:CONFIGURATION = 'Debug' to build Debug. The version comes from git tags (MinVer, v-prefixed).

param(
    [string]$Target = 'test',
    [string]$Runtime = ''
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "'dotnet' not found. Install the .NET 10 SDK from https://dot.net"
}

$Configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Release' }
$Out = if ($Runtime) { "artifacts/publish/$Runtime" } else { 'artifacts/publish/portable' }

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed (exit $LASTEXITCODE)" }
}

function Invoke-Build { Invoke-Dotnet build fuseraft-web.slnx -c $Configuration --nologo }

function Invoke-Test { Invoke-Dotnet test fuseraft-web.slnx -c $Configuration --nologo }

function Invoke-Publish {
    if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
    if ($Runtime) {
        Invoke-Dotnet publish FuseraftWeb.csproj -c $Configuration -r $Runtime --self-contained -o $Out --nologo `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
    } else {
        Invoke-Dotnet publish FuseraftWeb.csproj -c $Configuration -o $Out --nologo -p:DebugType=none
    }
    Write-Host "Published to $Out"
}

function Invoke-Pack {
    if (-not $Runtime) { throw 'pack needs a runtime, e.g. .\build.ps1 pack win-x64' }
    Invoke-Publish
    $version = (& dotnet msbuild FuseraftWeb.csproj -t:MinVer -getProperty:MinVerVersion "-p:Configuration=$Configuration" | Select-Object -Last 1).Trim()
    $name = "fuseraft-web-$version-$Runtime.zip"
    Compress-Archive -Path "$Out/*" -DestinationPath "artifacts/$name" -Force
    Write-Host "Packed artifacts/$name"
}

switch ($Target) {
    'build'   { Invoke-Build }
    'test'    { Invoke-Build; Invoke-Test }
    'publish' { Invoke-Publish }
    'pack'    { Invoke-Pack }
    default   { throw "Unknown target '$Target' (build | test | publish [rid] | pack <rid>)" }
}
