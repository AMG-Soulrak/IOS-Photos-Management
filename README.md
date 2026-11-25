# IOS Photos · Fotos del iPhone al PC

Programa personal para **Windows** que copia las fotos y vídeos de un iPhone o iPad al PC, **se puede cortar y reanudar**, comprueba lo copiado y ayuda a ordenarlo (álbumes, repetidas, descartes, personas). **Solo lee del iPhone: nunca borra ni escribe nada en él** (desde la v1.5).

Versión 1.5.0 · Licencia MIT (`LICENSE`) · Avisos de terceros en `THIRD-PARTY-NOTICES.md` · Texto para el usuario final en `LEEME.txt`.

## Qué hace

- **Copia** todo lo que hay en el carrete (`DCIM`) a una carpeta del PC. Guarda qué está copiado en `.iphotos\manifest.db` (dentro de esa carpeta); si se corta (cable, bloqueo, apagón), continúa solo con lo que falta.
- **Verifica**: calcula el SHA-256 durante la copia. «Verificar copias» relee lo del PC; «a fondo» relee también el iPhone y compara contenido. Lo que no coincide se vuelve a descargar **aparte** (el archivo dudoso no se toca).
- **Informe** `ultima-copia.txt` en la carpeta de destino tras cada copia: fecha y recuentos (sin número de serie, nombre del iPhone ni nombres de archivo).
- **Ordena** (funciones congeladas desde la v1.5: se corrigen fallos, no se añaden funciones): carpetas Año\Mes o por lugar, limpieza de clips de Live Photos, álbumes propios (carpetas en `Álbumes\`), repetidas y descartes (capturas, documentos). **Nada se borra**: se mueve a carpetas como `_Duplicados` o `_Descartadas`, siempre con simulación o confirmación previa.
- **Personas** (opcional, ver abajo): agrupa caras por parecido, todo en el PC.

Para liberar espacio en el iPhone: verifica la copia (mejor «a fondo»), pulsa **Abrir carpeta** para comprobarla y borra tú mismo desde la app Fotos. Si usas *Fotos de iCloud*, borrar en el iPhone también borra en iCloud.

## Requisitos

- Windows 10/11 de 64 bits. El ejecutable publicado lleva .NET dentro.
- **Controlador de Apple**: lo instala *Apple Devices* (Microsoft Store) o iTunes. Sin él ninguna técnica en vivo funciona.
- Cable de **datos**, iPhone **desbloqueado** y «**Confiar**» aceptado. Recomendado: *Ajustes → Fotos → Transferir a Mac o PC → Conservar originales*; con *Optimizar almacenamiento* de iCloud los originales pueden no estar en el teléfono.

## Técnicas de conexión

La app reconoce que el mismo iPhone visto de varias formas es un único aparato (por su UDID), prueba cada técnica y usa la mejor. Si una falla, pasa a la siguiente; el manifiesto es común, así que la siguiente solo copia lo que falta. Todas abren el dispositivo **solo en lectura**.

| Clave | Técnica | Notas |
|---|---|---|
| `wpd` | Windows Portable Devices (como el Explorador), con MediaDevices | Preferida en la selección automática. |
| `afc` | Acceso directo con libimobiledevice (iMobileDevice-net 1.3.17, de 2021) | **Avanzada y no verificada con iOS recientes**; lleva OpenSSL 1.1.1 sin soporte. Solo se usa si WPD no sirve o si la eliges. |
| `backup` | Última copia de seguridad de Apple Devices/iTunes | **Foto fija**: faltan las fotos posteriores. Solo si nada en vivo funciona. No lee copias cifradas. |
| `folder` | Una carpeta del PC que hace de iPhone | Solo para pruebas (`--source-folder`). |

## Qué sale a la red

**Nada, salvo una cosa que pides tú:** el botón para poner nombre a los lugares descarga `cities15000.zip` de **GeoNames** (HTTPS, CC BY 4.0). No sube fotos ni datos. No hay telemetría ni comprobación de actualizaciones. No pide ni guarda credenciales de Apple.

## Personas (datos biométricos)

Las huellas faciales son **datos sensibles** (RGPD, art. 9), también las de otras personas que salgan en tus fotos. Por eso:

- Viene **desactivado**. Antes del primer análisis se muestra un aviso y hay que aceptarlo (en la consola, `iphotos personas <carpeta> --activar`).
- Los datos (huellas, retratos, nombres) se guardan **fuera de la carpeta de fotos**, en `%LOCALAPPDATA%\IPhonePhotos\people\<huella de la carpeta>`, para que no viajen con las fotos a la nube o a un disco. Los datos de la v1.4 (`.iphotos\people`) se mueven ahí solos.
- **«Borrar todos los datos de Personas»** lo borra todo y vuelve a desactivarlo.
- La app **nunca pone un nombre sola**: si un grupo se parece a alguien ya nombrado, lo propone («Sí, es …») y tú confirmas.
- `faces.bin` anota en su cabecera el modelo usado; huellas de modelos distintos no se mezclan.

### Cómo obtener los modelos

Los modelos de caras **no vienen con la app**: el detector SCRFD (insightface) es «non-commercial research only». Copia en `%LOCALAPPDATA%\IPhonePhotos\models\`:

- `scrfd_2.5g_kps.onnx` (paquete NuGet `FaceAiSharp.Models.Scrfd.2dot5g_kps`),
- un `arcface*.onnx` (paquete `FaceAiSharp.Models.ArcFace.LResNet100E-IR-int8`),
- `open_closed_eye.onnx` (paquete `FaceAiSharp.Models.OpenVino.open-closed-eye-0001`, para valorar ojos en repetidas).

Si compilas el proyecto, NuGet los deja en `bin\...\onnx\` y la app también los busca ahí. Lee la licencia de cada modelo antes de usarlo. Sin modelos, Personas dice qué falta y dónde ponerlo; las repetidas funcionan sin valorar caras.

## Línea de comandos

```text
iphotos devices                          Dispositivos y estado del controlador de Apple
iphotos probe [--report archivo.txt]     Prueba la conexión (solo lectura); el informe no lleva serie ni nombre del iPhone
iphotos download <destino> [--adopt none|fast|deep] [--organize] [--nuevos] [--dry-run]
iphotos verify <destino> [--deep]        Comprueba lo copiado (--deep relee también el iPhone)
iphotos organize <carpeta> [--apply]     Año\Mes (sin --apply solo simula)
iphotos livephotos <carpeta> [--apply]   Aparta los .MOV de Live Photos (no los borra)
iphotos jpg <carpeta> [destino]          Copia JPG de cada HEIC, sin tocar los originales
iphotos album | album-add | duplicados | descartes | personas | frame   (ver iphotos --help)
```

Opciones comunes: `--device N`, `--only wpd|afc|backup`, `--no-snapshot`, `--backup-folder P`, `--source-folder P`. Los comandos `delete` y `probe-delete` se retiraron en la v1.5 (fallan con un mensaje).

**Adopción** (`--adopt`, «Si ya hay fotos» en la ventana): con `fast`, una foto que ya está en la carpeta con el mismo nombre, tamaño y **fecha** no se descarga otra vez, pero queda «**adoptada, sin verificar**» y no cuenta como verificada hasta que la verificación a fondo la compare con el iPhone. Con `deep` se compara el contenido antes de adoptarla.

## Dónde se guarda todo

- Fotos y control: en la carpeta de destino (`.iphotos\manifest.db`, registro de actividad, informes). Si un `manifest.db` de la v1.4 tenía fotos «borradas del iPhone», se copia antes a `manifest.db.bak-AAAAMMDD` y esos registros pasan a «verificado» (no se vuelven a descargar).
- Personas y modelos: `%LOCALAPPDATA%\IPhonePhotos\`.
- Ajustes: `iphotos.settings.json` junto al `.exe`. Errores de la ventana: `iphotos-error.log` junto al `.exe` (puede contener rutas de tus carpetas: revísalo antes de enviarlo).

## Límites conocidos

- Sin iPhone real en las pruebas automáticas: WPD y AFC solo se han probado a mano (la v1.4 descargó con un iPhone; la v1.5 no se ha probado con hardware). WPD abre ahora solo con `GenericRead`.
- Un archivo cortado a mitad se vuelve a copiar entero.
- La identidad de un archivo es ruta + tamaño + fecha; una técnica que no informa fechas no adopta nada por nombre y tamaño (puede copiar de más, nunca de menos).
- AFC depende de bibliotecas nativas de 2021 (OpenSSL 1.1.1 sin soporte).
- Ejecutables sin firmar: SmartScreen avisará.
- Organizar puede usar la fecha del archivo, que en fotos importadas o editadas con otras herramientas no siempre es la de la toma.

## Compilar

Con el SDK de .NET 8 instalado normalmente (no hace falta el SDK portable):

```powershell
dotnet build IPhonePhotos.sln
dotnet test tests/IPhonePhotos.Tests --filter "Category!=Perf"   # 85 pruebas
dotnet test tests/IPhonePhotos.Tests --filter "Category=Perf"    # escalado N/2N/4N (opcional)
```

Con el SDK portable local (opcional, no versionado): instala con `dotnet-install.ps1 -Channel 8.0 -InstallDir .\.dotnet` y ejecuta `. .\dev-env.ps1` antes de los comandos.

- `publish.ps1`: versión portable (un `.exe` por programa con .NET dentro). La versión sale de `Directory.Build.props`. No incluye los modelos ni las herramientas `.exe` de libimobiledevice (`Directory.Build.targets`).
- `build-installer.ps1`: instalador por usuario con Inno Setup 6.
- CI: `.github/workflows/ci.yml` (Windows, compila, pruebas, paquetes vulnerables y una barandilla que falla si reaparece código que borre en el dispositivo).

```text
src/IPhonePhotos.Core     Lógica (Windows: WPF para imágenes, WPD): técnicas, manifiesto, copia, verificación, organizar, Personas
src/IPhonePhotos.Cli      iphotos.exe
src/IPhonePhotos.App      IPhonePhotos.exe (WPF)
tests/IPhonePhotos.Tests  xUnit: dispositivos simulados con fallos, copias de seguridad falsas, CLI, migraciones
tools/                    Script que genera el logo y el icono
```

## Procedencia

La idea de copiar reproduciendo la estructura `DCIM` del iPhone se inspiró en el proyecto iPhoneCopy (MIT según su README). No se ha comparado el código con ese repositorio; si se reutilizó código suyo, hay que añadir su aviso MIT. El logo y el icono de la v1.5 se generan con `tools/make-icon.ps1` (dibujo propio).
