# Propuestas de mejora: más profesional y más atractiva

Documento de planteamiento. Nada de esto está construido. La demanda es una hipótesis hasta validarla con usuarios (véase «Cómo validarlo»). Las referencias a la competencia son de conocimiento general y no están comprobadas aquí.

## Posicionamiento (la base de todo)

La app ya tiene una ventaja que casi nadie ofrece: **copia verificada con SHA-256, solo lectura sobre el iPhone, sin nube y sin cuenta**. Lo atractivo para el usuario típico es la confianza («no me va a romper ni perder fotos») más la comodidad («lo hace sola»). Propuesta de frase: *«Tus fotos del iPhone en tu PC, comprobadas una a una. Sin nube, sin cuentas.»*

Problemas habituales de quien pasa fotos de iPhone a Windows:

| Problema típico | Qué hay hoy en la app |
|---|---|
| «No sé si se han copiado todas» | Verificación SHA-256 e informe (fuerte) |
| El iPhone se llena y no se atreve a borrar | No hay ayuda para liberar espacio |
| HEIC no se abre en otros programas | Conversión HEIC→JPG existe, como herramienta aparte |
| Copiar tarda y se corta con el cable | Reanuda sola; falta que sea automática y visible |
| Duplicados, ráfagas, capturas, vídeos pesados | Repetidas y Descartar existen, pero hay que buscarlos |
| «Me hago un lío con las carpetas» | Año\Mes, álbumes, lugar |
| «Perdí mis álbumes y favoritas del iPhone» | No se traen |
| Miedo a que una limpieza salga mal | Todo se aparta, no se borra; falta «Deshacer» |

## Propuestas, priorizadas

Esfuerzo: S = días, M = 1-2 semanas, L = más. Valor = impacto esperado sobre confianza o uso (juicio mío, sin datos).

### Núcleo: confianza y comodidad

1. **Asistente de primera copia (S-M, valor alto).** Pantalla de inicio en 3 pasos: (1) comprobar controlador de Apple y guiar si falta, (2) conectar y elegir destino, (3) copiar con progreso claro. Reduce el abandono del primer uso. Incluye detección de «desbloquea el iPhone y pulsa Confiar».
2. **Copia automática al conectar (M, alto).** Icono en la bandeja: al enchufar el iPhone, copia solo lo nuevo, verifica y avisa («312 fotos nuevas copiadas y verificadas»). Es lo que convierte la app en hábito.
3. **«Liberar espacio del iPhone» guiado (M, alto).** La app es de solo lectura y debe seguir siéndolo. Tras verificar a fondo, muestra «Estas 2.314 fotos (18 GB) están copiadas y verificadas: ya puedes borrarlas en el iPhone» con un informe y los pasos de Ajustes. Es el problema más evidente y no toca el teléfono.
4. **Deshacer (M, alto).** Diario de movimientos (ya se generan informes) y botón «Deshacer la última operación» para Organizar, Live Photos, Repetidas y Descartar. Elimina el miedo a ordenar.
5. **Panel de salud de la biblioteca (M, alto).** Pantalla inicial con cifras: fotos, GB, repetidas, capturas, vídeos grandes, sin fecha, y «espacio recuperable». Cada cifra abre su herramienta. Hace visibles funciones que hoy están escondidas.

### Funciones que resuelven dolores concretos

6. **Traer álbumes y favoritas del iPhone (L, alto, riesgo técnico).** Desde una copia de seguridad (`Photos.sqlite`) se pueden leer álbumes, favoritas y ocultas; crear carpetas en `Álbumes\`. Diferenciador fuerte; hay que comprobar el formato en versiones de iOS actuales.
6b. **Fotos editadas (S-M).** Detectar `IMG_E1234` junto a `IMG_1234` y los `.AAE`; ofrecer «quedarme con la editada y apartar la original».
7. **Compartir sin problemas (M, medio-alto).** «Exportar para compartir»: JPG, tamaño (correo, WhatsApp, impresión), **quitar la ubicación GPS** al exportar. Útil y fácil de explicar.
8. **Conversión al copiar (S, medio).** Opción «guardar también una copia JPG» en la propia descarga, en vez de herramienta aparte.
9. **Detector de fotos borrosas y casi iguales (M, medio).** Amplía Repetidas: nitidez baja, ojos cerrados (ya existe análisis de caras con consentimiento) y «escoge la mejor de cada ráfaga» con un clic por grupo.
10. **Corregir fechas (M, medio).** Fotos sin fecha o con fecha del archivo equivocada: proponer la de EXIF/nombre; renombrar por fecha (`2024-03-10_1200_IMG_0001`).
11. **Segunda copia 3-2-1 (M, medio).** «Copiar la biblioteca a un disco o NAS» con verificación; aviso si la última copia a un segundo destino tiene más de 30 días.
12. **Mapa y línea de tiempo (L, medio).** Vista por mapa (GPS ya se lee) y por línea de tiempo con búsqueda por persona, lugar, tipo (Live, vídeo, captura).
13. **Vídeos pesados (L, medio).** Listar los vídeos mayores y ofrecer conversión a un formato más compatible. Exige elegir un codificador y revisar licencias.

### Aspecto profesional

14. **Instalador firmado y actualizaciones (M).** Firma de código (evita el aviso de SmartScreen), versión única y comprobación de actualizaciones **opcional** y sin datos personales.
15. **Primer minuto cuidado (S).** Estados vacíos con una acción clara, mensajes de error en lenguaje normal (con «qué hacer»), pantalla «Qué hay de nuevo».
16. **Accesibilidad y modo oscuro completos (M).** Cerrar la lista de `TODO.md`: teclado, lector de pantalla, selección con ✓, diálogos y barras oscuros.
17. **Idiomas (M).** Inglés primero: ampliar mercado sin cambiar nada más.
18. **Confianza visible (S).** Página «Qué hace y qué no hace»: solo lectura, sin red salvo GeoNames, código abierto (MIT). Diagnóstico exportable (sin rutas ni datos) para pedir ayuda.
19. **Medición respetuosa (opcional, S).** Sin telemetría por defecto. Si se quiere saber qué se usa, contador local que el usuario pueda enviar a mano.

## Orden recomendado

| Fase | Contenido | Por qué primero |
|---|---|---|
| 1. Confianza (≈3-4 semanas) | 1 Asistente, 4 Deshacer, 5 Panel, 3 Liberar espacio, 15 Primer minuto, 16 Accesibilidad | Reducen el abandono y el miedo, y casi todo es interfaz y diario |
| 2. Hábito (≈3 semanas) | 2 Copia automática, 8 Conversión al copiar, 7 Compartir sin GPS, 6b Editadas | Hacen que se use cada semana |
| 3. Diferenciación (≈6+ semanas) | 6 Álbumes y favoritas, 9 Borrosas, 10 Fechas, 11 Segunda copia | Lo que otras apps no hacen bien |
| 4. Alcance | 14 Instalador firmado, 17 Inglés, 12 Mapa, 13 Vídeos | Distribución y escala |

## Riesgos y decisiones previas

- **La congelación de funciones** del README (Ordenar está congelado desde la v1.5) hay que levantarla o acotarla: la fase 1 y 2 añaden funciones.
- **Solo lectura:** «Liberar espacio» y «Copia automática» deben respetarla; no se propone borrar nada en el iPhone.
- **Licencias:** los pesos de InsightFace son «solo investigación»; ampliar Personas o venderlo exige otro modelo con licencia compatible (véase `TODO.md`). Un codificador de vídeo también tiene sus licencias.
- **Privacidad:** cualquier función con caras, ubicación o actualizaciones necesita aviso propio y revisión de `legal-privacidad`.
- **Dependencia de Apple:** el acceso depende del controlador de Apple y del formato de copia de iOS, que cambian. Hay que reservar mantenimiento.

## Cómo validarlo (barato, antes de construir)

1. Diez entrevistas de 20 minutos con gente que pase fotos del iPhone al PC; preguntar qué hicieron la última vez y qué les dio miedo.
2. Maqueta navegable del asistente y del panel de salud; medir si entienden qué hacer sin ayuda.
3. Página sencilla con la frase de posicionamiento y un botón de descarga para ver cuántos la usan (sin seguirles).
4. Revisar la lista de mejoras con quien ya use la app: ordenar las 19 por «me haría usarla más».

Siguiente paso posible: pasar la lista a `producto-lead` para validar demanda y competencia con investigación real, y a `diseno-lead` para maquetar el asistente y el panel.
