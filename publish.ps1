# Genera la versión PORTABLE: un único .exe por programa, con el runtime de .NET incluido
# (no hace falta instalar .NET ni tener permisos de administrador en el PC donde se use).
#
# Uso:   .\publish.ps1
# Salida: dist\IPhonePhotos-Portable\IPhonePhotos.exe   (ventana)
#         dist\IPhonePhotos-Portable\iphotos.exe        (línea de comandos)
#
# La versión sale de Directory.Build.props (fuente única). No se incluyen los modelos ONNX de caras ni las herramientas
# .exe de libimobiledevice (ver Directory.Build.targets).
param([string]$Version = "", [switch]$ForInstaller)
. "$PSScriptRoot\dev-env.ps1"

$root = $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version }
# -ForInstaller: sin comprimir (el instalador comprime todo junto y deduplica el runtime comun) y en otra carpeta,
# para no tocar la version portable.
$outName = if ($ForInstaller) { "dist\_installer-src" } else { "dist\IPhonePhotos-Portable" }
$out = Join-Path $root $outName
$compress = if ($ForInstaller) { "false" } else { "true" }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$common = @(
  "-c", "Release",
  "-r", "win-x64",
  "--self-contained", "true",
  "-p:PublishSingleFile=true",
  "-p:IncludeNativeLibrariesForSelfExtract=true",
  "-p:EnableCompressionInSingleFile=$compress",
  "-p:Version=$Version",
  "-p:DebugType=none",
  "-p:DebugSymbols=false"
)

foreach ($proj in @("IPhonePhotos.App", "IPhonePhotos.Cli")) {
  Write-Host "Publicando $proj ..." -ForegroundColor Cyan
  dotnet publish "$root\src\$proj\$proj.csproj" @common -o "$root\dist\_tmp\$proj" --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $proj" }
  Get-ChildItem "$root\dist\_tmp\$proj" -Filter *.exe | Copy-Item -Destination $out
}
Remove-Item "$root\dist\_tmp" -Recurse -Force

foreach ($doc in @("LEEME.txt", "LICENSE", "THIRD-PARTY-NOTICES.md")) { Copy-Item "$root\$doc" $out -ErrorAction SilentlyContinue }
if (Get-ChildItem $out -Recurse -Include *.onnx) { throw "No se deben empaquetar modelos ONNX (licencia no comercial)." }
Write-Host ""
Write-Host "Listo: $out" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
