# TODO

Pendiente tras la ronda de pruebas y correcciones (nov. 2026). Lo ya corregido está fijado por `tests/IPhonePhotos.Tests/RegressionTests.cs`.

## Necesita decisión del propietario

- [ ] **Repositorio:** `origin/main` publica ≈13.000 archivos (SDK, caché NuGet, binarios) con rutas del perfil. Decidir: reescribir el historial o publicar un repo nuevo desde `main-limpio` (107 archivos).
- [ ] **Licencias:** pesos SCRFD/ArcFace (InsightFace) «solo investigación no comercial»: no redistribuirlos ni enlazarlos como parte del producto sin decidirlo. ImageSharp con licencia dividida (Six Labors). `readline.dll` es GPL-3. Completar la atribución de GeoNames (CC BY 4.0). Revisar con abogado la titularidad del código (art. 97.4 LPI).
- [ ] **Privacidad:** README y aviso de Personas dicen «solo en este PC»; revisar el texto con `legal-privacidad` y valorar un aviso propio para Repetidas (ahora ya no analiza caras sin consentimiento).
- [x] **Congelación de funciones:** levantada para las Fases 1-2 (v1.6: copia, conexión, informe, fechas, JPG, ediciones). Álbumes, personas, repetidas y descartes siguen sin crecer.

## v1.6: pruebas manuales con el iPhone 13 (pendientes, nada de esto se ha probado en hardware)

- [ ] **Conexión WPD:** con el iPhone bloqueado, sin «Confiar», tras pulsar «No confiar» y sin Apple Devices: ¿el asistente da la causa correcta? (Con WPD solo se distingue «no se ve» / «no enseña fotos»; las causas finas salen de AFC.)
- [ ] **Copia completa** de la biblioteca real (≥ 1.000 elementos, con vídeos y Live Photos): tiempo, «X de Y · velocidad · tiempo restante» y lista «no copiados».
- [ ] **Corte de cable** a mitad y reanudación: espera, «Continuar», nada repetido (`CompletedCopies` solo lo comprueba en simulación).
- [ ] **iCloud optimizado** activado: ¿qué ve WPD/AFC de las fotos que solo están en iCloud? Hoy la app dice «no se pudo comprobar»; si alguna técnica da el recuento real, implementar `ICloudStateReporter` en ella.
- [ ] **Fechas reales:** HEIC y MOV del iPhone en «Año\Mes» por fecha de captura; revisar la zona horaria de los vídeos (se lee `mvhd`, UTC → hora local).
- [ ] **HEIC → JPG** con y sin «Extensiones de imagen HEIF»; aviso tras copiar.
- [ ] **IMG_E / .AAE** reales: ¿el iPhone los entrega así por WPD con «Conservar originales»?
- [ ] **Ventanas nuevas** (no hay pruebas automáticas de interfaz): «Ayuda de conexión» (reintento cada 4 s, «Diagnóstico», Microsoft Store), panel de error con «Detalles», vista grande (zoom, F11, ← →, Esc, HEIC, memoria al cerrar), botón «ampliar» con ratón, teclado y lector de pantalla, modo oscuro.

## v1.6: pendiente

- [ ] La guía de iOS cita rutas de Ajustes sin verificarlas en la versión de iOS del usuario.
- [ ] En el álbum, Espacio sigue marcando la foto (no la abre) para no romper la selección con teclado; Intro y doble clic abren la vista grande.
- [ ] La fecha de captura de vídeo no lee `com.apple.quicktime.creationdate` (con zona horaria).
- [ ] Commits de la v1.6 agrupados por capa (Core / ventana / miniaturas / documentación), no uno por punto.

## Seguridad

- [x] **DLL de AFC (v1.7):** ya no se rastrea `%TEMP%\.net` ni `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Se toma la carpeta exacta de extracción (`NATIVE_DLL_SEARCH_DIRECTORIES`, comprobado con una app de un solo archivo), se copian a `%LOCALAPPDATA%\IPhonePhotos\native\1.3.17\` solo las DLL cuyo SHA-256 es el del paquete iMobileDevice-net 1.3.17, y se carga solo de ahí (con segunda comprobación justo antes). **Límite:** otro proceso del mismo usuario puede escribir en `%LOCALAPPDATA%` entre la comprobación y la carga (ventana mínima). Al cambiar de versión del paquete hay que regenerar los hashes de `NativeVault`.
- [ ] **SQLitePCLRaw/SQLite:** sigue en 2.1.6 (SQLite 3.41.x): la caché local `.nuget` no tiene otra versión y no hay red. Actualizar `Microsoft.Data.Sqlite`/`SQLitePCLRaw` a una versión con SQLite ≥ 3.50.2 (CVE-2025-6965) con conexión, y actualizar `THIRD-PARTY-NOTICES.md`.
- [x] **CI (v1.7):** falla si `dotnet list package --vulnerable --include-transitive` encuentra paquetes vulnerables o no puede consultar. Si los avisos de NuGet marcan SQLitePCLRaw 2.1.6, el CI se pondrá en rojo hasta actualizarlo (no se pudo comprobar sin red).
- [x] **Modelos ONNX (v1.7):** SHA-256 al cargarlos contra los de los paquetes NuGet del README. Decisión: nombre conocido con otro contenido → no se carga (y se dice por qué); modelo con otro nombre (otro `arcface*.onnx`) → se usa y se muestra su hash en Personas y en el registro.
- [x] **`LocalRelativePath` (v1.7):** también se valida al leer: `ManifestStore` descarta rutas locales que salen de la carpeta (la copia elige otra dentro) e ignora registros con ruta de dispositivo insegura.
- [x] **Registro de errores (v1.7):** `iphotos-error.log` sustituye por defecto la carpeta temporal, la del perfil y el nombre de usuario. Las rutas fuera del perfil y los nombres de archivo se conservan.

## Rendimiento

Medido con `dotnet test --filter Category=Perf` (datos sintéticos, PC de 24 hilos, nov. 2026).

- [x] **Miniaturas (v1.7):** la cuadrícula del álbum solo guarda la imagen de las miniaturas visibles y 3 filas alrededor; las demás se sueltan y se vuelven a decodificar al acercarse. «Mostrar más» añade cuadros a la colección en vez de recrearlos todos. Los cuadros (sin imagen) siguen creándose todos: WPF no trae un panel envolvente virtualizado. **Memoria no medida** (no hay prueba de interfaz).
- [x] **Recarga tras cada operación (v1.7, medido, no se cambia):** `Album.Load` con la caché hecha tarda 12-14 ms con 1.200 fotos (frío 68-123 ms) y mover 50 fotos a un álbum 21-32 ms. Con las miniaturas soltadas fuera de la vista, recargar solo decodifica lo visible. Actualizar los datos en memoria sin releer no compensa el riesgo.
- [ ] **Personas:** medido 43-55 ms/foto con JPEG de 12 MP sin caras (decodificar + detector, `PeoplePerfTests`). Dos analizadores con modelos propios a la vez fueron ≈1,7 veces **más lentos** (ONNX Runtime ya reparte cada análisis entre núcleos), así que no se paraleliza. El ≈0,8 s/HEIC citado antes está sin medir en esta máquina: medir con HEIC reales con caras (WIC + ArcFace) antes de tocarlo.
- [ ] **Repetidas:** cada HEIC se decodifica varias veces; el coste depende de cuántas ráfagas haya (caras sobre los grupos).
- [x] **Renombrado de álbum (v1.7):** los cambios del manifiesto se preparan antes, se guardan en una sola transacción de LiteDB y, si fallan, la carpeta vuelve a su nombre. Un corte de luz entre renombrar la carpeta y guardar sigue pudiendo desparejarlos (la reconciliación de `FileMover` lo corrige al siguiente movimiento). La vuelta atrás no tiene prueba automática.

## Interfaz y accesibilidad

- [ ] Probar con teclado real (Tab, flechas, Espacio, Intro, tecla de menú) y con un lector de pantalla; las miniaturas ya tienen nombre accesible pero el `Border` contenedor no aparece como control.
- [x] **Alternativa de teclado (v1.7):** botón y opción de menú «Mover a álbum…» (menú con los álbumes, flechas e Intro); la tecla de menú o Mayús + F10 abre el menú de la miniatura y el de la lista de álbumes; F2 cambia el nombre del álbum. Sin probar con teclado real.
- [x] **Selección (v1.7):** marca ✓ además del borde; el borde y la marca usan el acento del modo (#5AA3F0 en oscuro) con `OnAccent` encima.
- [ ] Barras de desplazamiento, `MessageBox` y menús siguen claros en modo oscuro: `MessageBox` es del sistema (haría falta una ventana propia) y restilar barras y menús es un rediseño del tema. No hecho.
- [x] **Barras de progreso (v1.7):** nombre accesible en todas; LiveSetting también en los estados de Copiar, Repetidas, Organizar y Descartar.
- [x] **Textos (v1.7):** ninguno por debajo de 12 px (etiquetas de miniatura, LIVE, Descartar). Estados vacío y de error del álbum más claros. Pendiente: tamaños de título inconsistentes.
- [ ] Verificar a 960×700 que el ajuste de botones se ve bien (no se comprobó con captura).

## Calidad

- [ ] No hay pruebas automáticas de interfaz (filtros, multiselección, arrastre, Personas, Descartar, Repetidas).
- [ ] Sin probar: iPhone real (WPD/AFC), copia de seguridad con datos reales, teclado real, modo oscuro, DPI, instalador, rutas > 260 caracteres, «Organizar por lugar» de principio a fin (con fotos con GPS), detección de documentos con fotos reales, bibliotecas de 5.000+ fotos reales.
- [ ] Core depende de WPF (`UseWPF`). La regla «una operación a la vez» ya vive en Core (`LibraryGate`); en la interfaz solo quedan las dos líneas que piden el turno y muestran el mensaje en el estado de cada página (9 sitios). No se ha tocado en v1.7: juntarlas solo movería esas dos líneas.
- [ ] Una ejecución de las pruebas (1 de 6) falló en `Deep_verification_catches_a_mismatch_with_the_device` y `Photo_already_in_the_library_without_a_manifest_record_is_not_downloaded` y pasó al repetir: prueba inestable sin diagnosticar.
- [ ] Falta ADR-0001 y hay comentarios desactualizados.
- [ ] «Organizar por lugar»: GeoNames `cities15000` incluye distritos (Barcelona sale como «Eixample»); valorar nombres de ciudad.
