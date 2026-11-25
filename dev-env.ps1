# Prepara el entorno de desarrollo portable (SDK de .NET dentro de la carpeta del proyecto).
# Uso:  . .\dev-env.ps1   (con el punto delante, para que las variables persistan en la sesión)
$root = $PSScriptRoot
$env:DOTNET_ROOT = "$root\.dotnet"
$env:PATH = "$root\.dotnet;$env:PATH"
$env:NUGET_PACKAGES = "$root\.nuget\packages"
$env:DOTNET_CLI_HOME = "$root\.dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
Write-Host "Entorno listo. dotnet = $(dotnet --version)"
