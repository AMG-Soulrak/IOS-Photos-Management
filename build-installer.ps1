# Genera el INSTALADOR: dist\IPhonePhotos-Setup-<version>.exe  (un solo archivo, sin permisos de administrador).
#
# Uso:   .\build-installer.ps1                 (version por defecto)
#        .\build-installer.ps1 -Version 1.5.1     (si no se indica, la de Directory.Build.props)
#        .\build-installer.ps1 -SkipPublish    (reutiliza dist\IPhonePhotos-Portable ya publicado)
#
# Hace tres cosas: 1) publica los programas (publish.ps1), 2) localiza Inno Setup (lo instala con winget,
# solo para tu usuario, si falta) y 3) compila installer\IPhonePhotos.iss.
param(
  [string]$Version = "",
  [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version }

if (-not $SkipPublish) {
  & "$root\publish.ps1" -Version $Version -ForInstaller
  if ($LASTEXITCODE -ne 0) { throw "Fallo la publicacion" }
}
foreach ($f in @("IPhonePhotos.exe", "iphotos.exe", "LEEME.txt", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
  if (-not (Test-Path "$root\dist\_installer-src\$f")) { throw "Falta dist\_installer-src\$f (ejecuta sin -SkipPublish)" }
}

function Find-Iscc {
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
  Write-Host "Inno Setup no esta instalado. Instalando con winget (solo para tu usuario)..." -ForegroundColor Cyan
  winget install --id JRSoftware.InnoSetup --exact --scope user --silent --accept-package-agreements --accept-source-agreements
  $iscc = Find-Iscc
  if (-not $iscc) { throw "No se encontro ISCC.exe tras instalar Inno Setup. Instalalo desde https://jrsoftware.org/isdl.php" }
}

Write-Host "Compilando instalador $Version con $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "$root\installer\IPhonePhotos.iss"
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion del instalador" }

$setup = Join-Path $root "dist\IPhonePhotos-Setup-$Version.exe"
Write-Host ""
Write-Host "Listo: $setup" -ForegroundColor Green
Get-Item $setup | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
