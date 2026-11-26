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

- [ ] **DLL de AFC:** `AfcBackend` busca `imobiledevice.dll` (la más reciente) en `%TEMP%\.net` y en `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Una DLL plantada ahí se cargaría. La app es de un solo archivo, así que no se puede quitar sin rediseñar (p. ej. extraer a una carpeta propia con comprobación de hash).
- [ ] **SQLitePCLRaw/SQLite:** actualizar a una versión sin CVE conocidos (revisar CVE-2025-6965) y hacer que el CI falle con paquetes vulnerables, no solo informe.
- [ ] **Modelos ONNX:** verificar su hash SHA-256 al cargarlos.
- [ ] **`LocalRelativePath`** del manifiesto se valida al escribir, no al leer.
- [ ] **Registro de errores** (`iphotos-error.log`) con rutas completas: ofrecer anonimizar.

## Rendimiento

- [ ] **Miniaturas:** siguen sin liberarse al pulsar «Mostrar más» (≈0,35 MB/foto a 200 % de escala, menos a 100 %). Valorar cuadrícula virtualizada y caché de miniaturas en disco.
- [ ] **Recarga completa** de biblioteca y manifiesto tras cada operación.
- [ ] **Personas:** ≈0,8 s por HEIC (analiza de una en una bajo un bloqueo); ≈27 min para 2.000 fotos. Paralelizar el reconocimiento.
- [ ] **Repetidas:** cada HEIC se decodifica varias veces; el coste depende de cuántas ráfagas haya (caras sobre los grupos).
- [ ] **Renombrado de álbum** no es atómico con el manifiesto.

## Interfaz y accesibilidad

- [ ] Probar con teclado real (Tab, flechas, Espacio, Intro) y con un lector de pantalla; las miniaturas ya tienen nombre accesible pero el `Border` contenedor no aparece como control.
- [ ] Arrastrar a un álbum y menús contextuales solo con ratón: dar alternativa de teclado («Mover a álbum…»).
- [ ] Selección solo por color (#005FB8 fijo): añadir marca visible (✓) y contraste en modo oscuro.
- [ ] Barras de desplazamiento, `MessageBox` y menús siguen claros en modo oscuro.
- [ ] Barras de progreso sin nombre accesible; LiveSetting solo en el estado del álbum.
- [ ] Textos de 10 px; tamaños de título inconsistentes; estados vacíos y de error pobres.
- [ ] Verificar a 960×700 que el ajuste de botones se ve bien (no se comprobó con captura).

## Calidad

- [ ] No hay pruebas automáticas de interfaz (filtros, multiselección, arrastre, Personas, Descartar, Repetidas).
- [ ] Sin probar: iPhone real (WPD/AFC), copia de seguridad con datos reales, teclado real, modo oscuro, DPI, instalador, rutas > 260 caracteres, «Organizar por lugar» de principio a fin (con fotos con GPS), detección de documentos con fotos reales, bibliotecas de 5.000+ fotos reales.
- [ ] Core depende de WPF (`UseWPF`); la regla «una operación a la vez» está repetida en 7 sitios de la interfaz: subirla a Core.
- [ ] Falta ADR-0001 y hay comentarios desactualizados.
- [ ] «Organizar por lugar»: GeoNames `cities15000` incluye distritos (Barcelona sale como «Eixample»); valorar nombres de ciudad.
