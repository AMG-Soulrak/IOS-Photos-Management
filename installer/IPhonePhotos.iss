; Instalador de "Fotos del iPhone" (Inno Setup 6).
; Se compila con build-installer.ps1, que pasa la version de Directory.Build.props (/DAppVersion=...).
;
; - Instala PARA EL USUARIO ACTUAL: no pide permisos de administrador ni toca Program Files.
; - Incluye los dos programas (ventana y consola), cada uno en un unico .exe con .NET incluido.
; - No incluye el controlador de Apple (lo instala "Apple Devices" de la Microsoft Store: no se puede redistribuir).
; - No incluye los modelos de caras (licencia no comercial): cada usuario los obtiene aparte (README).

#ifndef AppVersion
  #error Falta /DAppVersion: compila con build-installer.ps1 (la version sale de Directory.Build.props).
#endif

#define AppName "Fotos del iPhone"
#ifndef Dist
  #define Dist "..\dist\_installer-src"
#endif

[Setup]
AppId={{B7D2E4A1-6C35-4F8E-9A12-5D3C7E8F0B46}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=AMG-Soulrak
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\IPhonePhotos
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=IPhonePhotos-Setup-{#AppVersion}
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\IPhonePhotos.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el &escritorio"; GroupDescription: "Accesos directos:"
Name: "addtopath"; Description: "Poder usar el comando &iphotos desde la consola (añadir al PATH del usuario)"; GroupDescription: "Opciones avanzadas:"; Flags: unchecked

[Files]
Source: "{#Dist}\IPhonePhotos.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\iphotos.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\LEEME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#Dist}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Dist}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\IPhonePhotos.exe"
Name: "{autoprograms}\{#AppName} - Léeme"; Filename: "{app}\LEEME.txt"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\IPhonePhotos.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
; El controlador de Apple es la unica dependencia externa: si no esta, se ofrece abrir su pagina en la Microsoft Store.
Filename: "ms-windows-store://pdp/?productid=9NP83LWLPZ9K"; Description: "Instalar Apple Devices (necesario para que Windows vea el iPhone)"; Flags: shellexec postinstall skipifsilent; Check: not AppleSupportInstalled
Filename: "{app}\IPhonePhotos.exe"; Description: "Abrir {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Archivos que la aplicacion crea junto al .exe al usarse.
Type: files; Name: "{app}\iphotos.settings.json"
Type: files; Name: "{app}\iphotos-error.log"
Type: files; Name: "{app}\informe-conexion.txt"

[Code]
// ¿Esta instalado el soporte de Apple para iPhone? Dos variantes:
//  - iTunes: servicio de Windows "Apple Mobile Device Service".
//  - Apple Devices (Microsoft Store): paquete AppleInc.AppleDevices.
function AppleSupportInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service');
  if Result then Exit;
  if RegGetSubkeyNames(HKCU,
    'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('appleinc.appledevices_', Lowercase(Names[I])) = 1 then
      begin
        Result := True;
        Exit;
      end;
end;

// Evita duplicar la carpeta en el PATH del usuario.
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    Exit;
  end;
  Result := Pos(';' + Lowercase(Param) + ';', ';' + Lowercase(OrigPath) + ';') = 0;
end;

// Al desinstalar, quita la carpeta del PATH (solo si se habia añadido).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath, AppDir, NewPath: string;
  P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then Exit;
  AppDir := ExpandConstant('{app}');
  NewPath := ';' + OrigPath + ';';
  P := Pos(';' + Lowercase(AppDir) + ';', Lowercase(NewPath));
  if P = 0 then Exit;
  Delete(NewPath, P, Length(AppDir) + 1);
  NewPath := Copy(NewPath, 2, Length(NewPath) - 2);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath);
end;
