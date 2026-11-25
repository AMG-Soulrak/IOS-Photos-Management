# Avisos de terceros

El código de IOS Photos está bajo licencia MIT (ver `LICENSE`). Usa los componentes de abajo. La licencia de cada paquete NuGet se ha leído de su `.nuspec` (y de su archivo de licencia cuando el paquete lo trae) en la caché local, versión exacta del proyecto. **«No leído»** significa que el paquete no trae el texto y no se ha podido comprobar sin conexión: hay que verificarlo antes de distribuir.

Generado para la versión 1.5.0. No es asesoramiento legal.

## Paquetes .NET que van dentro del programa

| Paquete | Versión | Licencia | Notas |
|---|---|---|---|
| MediaDevices | 2.0.0 | MIT | Técnica WPD. |
| iMobileDevice-net | 1.3.17 | No leído (el `.nuspec` solo enlaza a `LICENSE.txt` del repositorio; según el proyecto, LGPL-2.1) | Envoltorio de libimobiledevice (técnica AFC). Ver nativos abajo. |
| LiteDB | 5.0.21 | MIT (archivo `LICENSE` del paquete) | Manifiesto de reanudación. |
| Microsoft.Data.Sqlite / .Core | 8.0.11 | MIT | Lectura de `Manifest.db` de las copias de seguridad. |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.6 | Apache-2.0 | Incluye `e_sqlite3.dll` (SQLite, dominio público). |
| System.ServiceProcess.ServiceController | 8.0.1 | MIT | |
| System.Diagnostics.EventLog | 8.0.1 | MIT | |
| System.Numerics.Tensors | 10.0.8 | MIT | |
| System.Buffers / System.Memory | 4.5.1 / 4.5.5 | MIT (enlace a la licencia de dotnet/corefx; `LICENSE.TXT` incluido) | |
| FaceAiSharp | 0.6.38 | MIT | Detección de caras y huellas (Personas, repetidas). |
| Microsoft.ML.OnnxRuntime / .Managed | 1.26.0 | MIT (archivo `LICENSE`); trae su propio `ThirdPartyNotices.txt` | |
| SixLabors.ImageSharp | 3.2.0 | **Six Labors Split License 1.0** (archivo `LICENSE`) | Transitivo de FaceAiSharp. Gratis para código abierto y para quien no supere el umbral de ingresos que fija la licencia; uso comercial por encima de ese umbral exige licencia de pago. |
| SixLabors.ImageSharp.Drawing / SixLabors.Fonts | 2.2.0 / 2.2.0 | Six Labors Split License 1.0 | Transitivos de FaceAiSharp. |
| MathNet.Numerics | 5.0.0 | MIT | Transitivo. |
| SimpleSIMD | 3.3.1 | MIT | Transitivo. |
| CommunityToolkit.Diagnostics | 8.4.2 | MIT | Transitivo. |

## Bibliotecas nativas redistribuidas (dentro del ejecutable, carpeta `runtimes\win-x64\native` de iMobileDevice-net)

Desde la v1.5 **no** se incluyen las herramientas `.exe` del paquete (`ideviceinstaller.exe`, `usbmuxd.exe`, `idevicerestore.exe`…). Sí van sus DLL, porque `imobiledevice.dll` las carga. El paquete no incluye los textos de licencia: **todas «no leído»**. Licencias habituales de esos proyectos, a verificar:

| DLL | Proyecto | Licencia habitual (no leída) |
|---|---|---|
| imobiledevice.dll, plist.dll, usbmuxd.dll, irecovery.dll, ideviceactivation.dll | libimobiledevice, libplist, libusbmuxd, libirecovery, libideviceactivation | LGPL-2.1 (se usan como DLL sin modificar) |
| libcrypto-1_1-x64.dll, libssl-1_1-x64.dll | OpenSSL 1.1.1 | OpenSSL/SSLeay (aviso obligatorio). Versión sin soporte desde 2023. |
| libcurl.dll | curl | Licencia curl (tipo MIT) |
| libxml2.dll | libxml2 | MIT |
| zlib1.dll, zip.dll, bz2.dll, lzma.dll | zlib, libzip, bzip2, xz | zlib / BSD-3 / tipo BSD / dominio público |
| iconv-2.dll | libiconv | LGPL |
| libusb-1.0.dll, libusb0.dll | libusb | LGPL-2.1 |
| pcre.dll, pcreposix.dll | PCRE | BSD |
| readline.dll | GNU readline | **GPL-3** (verificar si hace falta: parece usarse solo por herramientas que ya no se incluyen) |
| getopt.dll, pthreadVC3.dll | getopt, pthreads-win32 | Varía / LGPL |
| vcruntime140.dll | Microsoft Visual C++ Runtime | Redistribuible de Microsoft |

## No incluidos (los obtiene cada usuario)

| Componente | Licencia | Por qué no se incluye |
|---|---|---|
| Modelo SCRFD 2.5g (detector de caras, insightface) | «non-commercial research only» (archivo `LICENSE.md` del paquete FaceAiSharp.Models.Scrfd.2dot5g_kps) | No se puede redistribuir con una app MIT de uso libre. |
| Modelo ArcFace LResNet100E-IR int8 (huellas faciales, ONNX Model Zoo) | El paquete declara Apache-2.0; el modelo se entrenó con datos de insightface/MS1M, cuyos términos no se han leído | Por prudencia, igual que SCRFD. |
| Modelo open-closed-eye-0001 (Intel OpenVINO) | Apache-2.0 (README del paquete) | Podría incluirse; se deja fuera para que todos los modelos estén en el mismo sitio. |
| Controlador de Apple (Apple Devices / iTunes) | Propietaria de Apple | No redistribuible. |
| SDK de .NET | Licencia de Microsoft | Solo para compilar; no se versiona. |

## Datos descargados a petición del usuario

- **GeoNames** (`cities15000.zip`, para poner nombre a los lugares): CC BY 4.0, © GeoNames (<https://www.geonames.org>). Solo se descarga si el usuario pulsa el botón correspondiente.
