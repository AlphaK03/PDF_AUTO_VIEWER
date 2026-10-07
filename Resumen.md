# Philips Document Flow (PdfAutoViewer): flujo de la app y casos de prueba

## 1. Qué hace

Es una app de bandeja del sistema (.NET 8 WinForms). Vigila la carpeta Descargas del usuario, abre cada PDF nuevo en un visor propio (WebView2) y **borra el archivo cuando se cierra el visor**. Si llegan versiones del mismo documento en varios idiomas (`_SPA`/`_ENG`), en PDF nativo y en PDF generado desde Word (`_docx.pdf`), o si llega una copia más nueva (`(1)`, `(2)`), decide cuál se queda abierta.

## 2. Flujo completo

### 2.1 Arranque ([Program.cs](PdfAutoViewer/Program.cs))

1. **Una sola instancia por sesión de Windows** (mutex `Local\PdfAutoViewer_SingleInstance`). Si ya hay una abierta en la sesión, la segunda termina sin avisar.
2. Registra manejadores de excepciones globales. Los errores se escriben en `%LOCALAPPDATA%\PdfAutoViewer\app-error.log`.
3. `StartupManager.EnableStartup()` escribe la ruta del `.exe`, entre comillas, en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PdfAutoViewer`. **Lo hace en cada arranque.**
4. `TrayApp` ([TrayApp.cs](PdfAutoViewer/UI/TrayApp.cs)):
   - Carga `settings.json`. Si falta o está corrupto, usa los valores por defecto (idioma preferido = **SPA**).
   - Crea el icono de bandeja con el menú "Show window" / "Exit".
   - Arranca `FolderMonitor` sobre la carpeta Descargas real (leída del registro, con `%USERPROFILE%\Downloads` como respaldo).
   - Muestra `StatusForm`.
   - Hace `PdfViewerForm.Prewarm()`: una ventana WebView2 invisible que mantiene caliente el proceso del navegador.

### 2.2 Detección ([FolderMonitor.cs](PdfAutoViewer/Core/FolderMonitor.cs))

- `FileSystemWatcher` sobre `*.pdf` escucha dos eventos:
  - **Created**: el archivo aparece directamente.
  - **Renamed**: la descarga termina y `.crdownload` pasa a `.pdf`.
- **Re-escaneo cada 3 s** como red de seguridad. Vuelve a disparar los PDF modificados en los últimos 3 min y después del arranque (con 5 s de margen). Por eso los PDF que ya estaban antes de arrancar **no** se abren.
- Si el watcher se desborda (error), re-escanea en ese momento.
- No vigila subcarpetas.

### 2.3 Programación y deduplicación ([PdfLifecycleManager.cs](PdfAutoViewer/Core/PdfLifecycleManager.cs), `Schedule`)

Un evento se descarta en tres casos:

- La ruta ya se está procesando (`_inProgress`).
- Terminó hace menos de 5 s (`_cooldown`).
- Ya se manejó y su `LastWriteTime` no cambió (`_handled`). Una re-descarga cambia la fecha, así que sí se procesa.

Cada PDF corre en su propia `Task`.

### 2.4 Ciclo de vida de un PDF

1. **Estabilización** (hasta 5 min, sondeo cada 150 ms):
   - Si el archivo desaparece, el ciclo no se completa y no aplica cooldown. Esto cubre el placeholder de 0 bytes de Edge.
   - Con 100 bytes o más: si se puede abrir con `FileShare.Read`, la descarga está lista. Si no, espera a que el tamaño no cambie en 2 lecturas seguidas.
   - Por debajo de 100 bytes nunca se considera listo, y a los 5 min se abandona.
2. **Apertura**: `PdfViewerForm.ShowAndWait` ([PdfViewerForm.cs](PdfAutoViewer/UI/PdfViewerForm.cs)) crea un hilo STA con su ventana maximizada, que toma el foco. El hilo de fondo se queda bloqueado hasta que la ventana se cierra.
3. **Inicialización de WebView2** con timeout de 20 s. Si falla:
   - Se registra en `%LOCALAPPDATA%\PdfAutoViewer\viewer-error.log`.
   - Aparece un globo de error.
   - **El archivo NO se borra**. No hay alternativa con Edge, por diseño.
4. **Reconciliación**, al quedar lista cada ventana. Se aplican tres reglas en este orden:

| # | Regla | Clave | Resultado |
|---|---|---|---|
| 1 | Copia más nueva | `DocumentKey` (sin `(n)`, conserva el idioma) | La ventana nueva cierra las anteriores del mismo documento |
| 2 | Idioma (solo si la preferencia ≠ Any y el archivo tiene idioma) | `PairingKey` (sin `(n)` ni `_SPA/_ENG`) | El preferido cierra al no preferido; el no preferido se cierra solo si ya está abierto el preferido |
| 3 | Tipo docx | `TypeGroupKey` (sin `_docx` ni `(n)`) | `_docx.pdf` gana sobre el `.pdf` nativo del mismo documento e idioma |

5. **Límite de visualización**: a los **15 min** sale un globo de advertencia (la única notificación rutinaria). A los **20 min** la ventana se cierra sola.
6. **Borrado** al cerrarse la ventana, sea por el usuario, por una regla o por el límite de tiempo:
   - Se borra el archivo y sus duplicados `nombre (n).pdf`, excepto los que otra tarea esté procesando.
   - Hasta 5 reintentos (~9 s).
   - Si sigue bloqueado (Defender, OneDrive), pasa a una cola que el **janitor** reintenta cada 20 s. No borra el archivo si fue reemplazado por una descarga nueva.

### 2.5 Interfaz

- **StatusForm** ([StatusForm.cs](PdfAutoViewer/UI/StatusForm.cs)):
  - Muestra "● ACTIVE" y la carpeta vigilada.
  - Tiene un combo de idioma (Any / SPA / ENG) que guarda `settings.json` al instante.
  - Cerrar con la × solo la oculta.
- **Bandeja**:
  - Doble clic o "Show window" vuelve a mostrar la ventana.
  - "Exit" detiene el monitor, cancela las tareas y sale.
- **Globos**: solo para la advertencia de 15 min (`warning`) y para errores (`error`). Los eventos detected, opened y deleted no notifican nada.

## 3. Casos de prueba

### A. Arranque e instancia

| ID | Caso | Resultado esperado |
|---|---|---|
| A1 | Primer arranque sin `settings.json` | Idioma = Spanish (_SPA); se muestran la ventana y el icono |
| A2 | Lanzar una segunda instancia en la misma sesión | La segunda termina sin UI; solo queda un icono |
| A3 | Dos sesiones distintas (RDP/VDI) | Cada sesión tiene su propia instancia |
| A4 | `settings.json` corrupto | Arranca con los valores por defecto, sin error |
| A5 | Revisar `HKCU\...\Run\PdfAutoViewer` después de arrancar | Ruta del exe entre comillas |
| A6 | Borrar la entrada de Run y relanzar a mano | Se vuelve a crear (comportamiento actual, ver §5) |
| A7 | Cerrar sesión y volver a iniciarla | La app arranca sola |
| A8 | Carpeta Descargas movida (p. ej. a D:\) | Vigila la carpeta real, que aparece en StatusForm |
| A9 | PDFs que ya estaban en Descargas antes de arrancar | **No** se abren |

### B. Detección y descarga

| ID | Caso | Resultado esperado |
|---|---|---|
| B1 | Descargar un PDF con Edge | Se abre en ≤ ~1–2 s, maximizado y con el foco |
| B2 | Descargar con Chrome (`.crdownload` → `.pdf`) | Se abre una vez |
| B3 | Copiar un PDF a Descargas con el Explorador | Se abre |
| B4 | PDF grande o red lenta | Solo se abre al terminar la descarga, sin errores |
| B5 | Descargar un archivo no PDF (.docx, .zip) | Se ignora |
| B6 | Extensión en mayúsculas (`.PDF`) | Se abre |
| B7 | PDF en una subcarpeta de Descargas | Se ignora |
| B8 | 5 o más PDFs de documentos distintos a la vez | Se abren todos, cada uno en su ventana |
| B9 | Archivo de menos de 100 bytes | No se abre; se abandona a los 5 min y no se borra |
| B10 | Descarga cancelada a mitad | No se abre nada ni aparece error |
| B11 | El mismo PDF no se reabre en bucle | Una sola ventana, aunque el re-escaneo lo vuelva a ver |

### C. Visor

| ID | Caso | Resultado esperado |
|---|---|---|
| C1 | Cerrar el visor con la × | El archivo se borra de Descargas |
| C2 | Navegación, zoom y búsqueda dentro del PDF | Funcionan (motor PDF de Chromium) |
| C3 | Sin WebView2 Runtime (Win10 sin instalar) | Globo de error; el archivo **se conserva**; queda en `viewer-error.log` |
| C4 | Primer PDF justo después de arrancar | Abre rápido gracias al prewarm |
| C5 | PDF protegido con contraseña o corrupto | Documentar el comportamiento (WebView2 pide la contraseña o muestra un error; la ventana sí abre) |

### D. Límite de 20 minutos

| ID | Caso | Resultado esperado |
|---|---|---|
| D1 | Dejar abierto 15 min | Globo "will close in 5 minutes" |
| D2 | Dejar abierto 20 min | La ventana se cierra y el archivo se borra |
| D3 | Cerrar antes de los 15 min | No aparece advertencia |
| D4 | Llega una copia nueva a los 10 min | La nueva ventana reinicia el contador a 0 |

> Los tiempos son constantes fijas (`WarnAfterMs`, `CloseAfterMs`). Para probar rápido, compila una versión con valores reducidos.

### E. Copias más nuevas

| ID | Caso | Resultado esperado |
|---|---|---|
| E1 | `doc.pdf` abierto y se descarga `doc (1).pdf` | Se cierra `doc.pdf` y queda `(1)`; `doc.pdf` se borra |
| E2 | Cerrar `doc (1).pdf` | Se borran `doc (1).pdf` y los demás `doc (n).pdf` que queden |
| E3 | `doc_SPA.pdf` abierto y llega `doc_ENG.pdf` con preferencia Any | Ambos abiertos (distinto `DocumentKey`) |
| E4 | Re-descargar el mismo nombre después de cerrarlo | Se abre de nuevo (fecha distinta) |

### F. Idioma (preferencia SPA / ENG / Any)

| ID | Caso | Resultado esperado |
|---|---|---|
| F1 | Pref SPA, llegan `X_SPA` y `X_ENG` a la vez | Queda solo SPA; ENG se cierra **y se borra** |
| F2 | Pref SPA, llega primero ENG y después SPA | Se cierra ENG y queda SPA |
| F3 | Pref SPA, llega primero SPA y después ENG | ENG se cierra solo |
| F4 | Pref SPA, llega solo `X_ENG` | Se abre ENG |
| F5 | Pref ENG, con las mismas variantes | Simétrico a F1–F3 |
| F6 | Pref Any, llegan los dos | Ambos abiertos |
| F7 | Formatos: `_SPA_`, `_SPA ` (espacio), `_SPA` al final, `_spa` | Se detectan; `_SPANISH` **no** |
| F8 | Documentos distintos con idiomas distintos | No interfieren |
| F9 | Cambiar la preferencia con documentos abiertos | Aplica solo a las ventanas que se abran después |
| F10 | La preferencia persiste tras reiniciar | Se lee de `settings.json` |
| F11 | `X_SPA (1).pdf` + `X_ENG.pdf` | Se emparejan (misma `PairingKey`) |

### G. Tipo docx

| ID | Caso | Resultado esperado |
|---|---|---|
| G1 | Llegan `X_SPA.pdf` y `X_SPA_docx.pdf` | Queda `_docx`; el nativo se cierra y se borra |
| G2 | Llega primero `_docx` y después el nativo | El nativo se cierra solo |
| G3 | `X_SPA_docx.pdf` + `X_ENG.pdf` | Son grupos distintos y el tipo no se filtra; aplica la regla de idioma |
| G4 | Combinación de 4 archivos (SPA/ENG × nativo/docx) con pref SPA | Solo queda `X_SPA_docx` |
| G5 | `X_docx (1).pdf` | Se reconoce como docx |
| G6 | `reportdocx.pdf` (sin separador) | **No** es docx |

### H. Borrado y bloqueos

| ID | Caso | Resultado esperado |
|---|---|---|
| H1 | Archivo bloqueado al cerrarlo (p. ej. abierto en otro programa) | Globo "locked — will keep retrying"; se borra al liberarlo (≤ 20 s) |
| H2 | Bloqueado y re-descargado antes de que el janitor lo borre | No se borra la versión nueva por error |
| H3 | Descargas en OneDrive | Se borra correctamente (quizá vía janitor) |
| H4 | Duplicados `X (2).pdf` sin abrir mientras `X.pdf` se cierra | Se borran junto con `X.pdf` |

### I. Interfaz y salida

| ID | Caso | Resultado esperado |
|---|---|---|
| I1 | Cerrar StatusForm con la × o con "Hide" | Se oculta y la app sigue |
| I2 | Doble clic en el icono / "Show window" | La ventana vuelve al frente |
| I3 | "Exit" sin documentos abiertos | El icono desaparece y el proceso termina |
| I4 | "Exit" con documentos abiertos | Los visores se cierran; comprobar si el archivo se borra (ver §5) |
| I5 | La versión mostrada coincide con `<Version>` del csproj | Correcto |

### J. Robustez 24/7 y despliegue

| ID | Caso | Resultado esperado |
|---|---|---|
| J1 | Uso continuo de más de 24 h con cientos de PDFs | Memoria estable; sin ventanas huérfanas |
| J2 | Ráfaga de más de 50 PDFs (desbordamiento del watcher) | Todos procesados gracias al re-escaneo |
| J3 | Descargas no disponible un momento | Se recupera en el siguiente barrido |
| J4 | Builds x64 / x86 (Wyse), light y fixed | Arrancan y abren PDFs en el equipo destino |
| J5 | Win10 sin .NET con el build fixed | Funciona |
| J6 | Usuario sin permisos de administrador | Todo funciona (HKCU y LocalAppData) |

## 4. Cobertura automática actual

Hay tests xUnit en [PdfAutoViewer.Tests](PdfAutoViewer.Tests). Cubren la **lógica pura**:

- Detección de idioma, `PairingKey`, `DocumentKey`, `TypeGroupKey`, `IsDocxType` y `StripNumericSuffix`.
- Serialización de ajustes.
- `BuildStartupValue`.

**Sin tests automáticos:**

- Deduplicación de `Schedule`, estabilización, borrado con duplicados y janitor.
- Reconciliación entre ventanas.
- Temporizadores.

Todo eso depende de la interfaz o del sistema de archivos. Para cubrirlo habría que:

1. Extraer la decisión de `RegisterAndReconcile` a una función pura (lista de viewers abiertos más el nuevo → cuáles cerrar), lo que permitiría testear F, G y E en unidades.
2. Sacar los tiempos (15/20 min, 3 s, 5 s, 20 s) a parámetros.
3. Hacer tests de integración de `PdfLifecycleManager` contra una carpeta temporal y un visor simulado.

## 5. Riesgos detectados en el código (probar a propósito)

1. **Carpeta Descargas inexistente**: si el valor del registro no existe, el respaldo `%USERPROFILE%\Downloads` puede no existir tampoco. En ese caso `new FileSystemWatcher(folder)` lanza una excepción dentro del constructor de `TrayApp`, y la app probablemente no arranca.
2. **"● ACTIVE" siempre está fijo**: `StatusForm` no refleja el estado real del monitor.
3. **La regla de idioma no depende del tiempo**: si el operador lleva 10 min leyendo `X_ENG` y alguien descarga `X_SPA`, la ventana ENG se cierra y el archivo se borra. Lo mismo pasa con la regla docx. Conviene confirmar que es lo esperado.
4. **Los archivos descartados por las reglas se borran** aunque nadie los haya visto (por diseño, pero vale la pena validarlo con los usuarios).
5. **"Exit" con visores abiertos**: la cancelación cierra las ventanas y la tarea de fondo intenta borrar el archivo mientras la app ya está saliendo. Hay una condición de carrera, así que el archivo puede quedar o no.
6. **Registro de arranque**: se reescribe en cada ejecución, así que borrar la entrada de Run a mano no sirve si alguien vuelve a lanzar la app. Desactivarla desde Configuración → Inicio sí se respeta, porque usa otra clave.
7. **Idioma por defecto SPA**: el hint dice "Any (open both)" como primera opción, pero el valor inicial es SPA.
