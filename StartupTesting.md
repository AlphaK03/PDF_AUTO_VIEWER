# Inicio automático en Wyse: diagnóstico y solución

Guía para averiguar por qué **Philips Document Flow (PDF)** no arranca solo al iniciar sesión en las Wyse, y cómo corregirlo.

- **Parte A**: la puede hacer cualquier usuario, **sin permisos de administrador**.
- **Parte B**: la tiene que hacer **TI o alguien con admin**.

---

## 1. Cómo funciona hoy

| Qué | Dónde |
|---|---|
| Código que registra el inicio | [`PdfAutoViewer/Core/StartupManager.cs`](PdfAutoViewer/Core/StartupManager.cs), método `EnableStartup()` |
| Quién lo llama | [`PdfAutoViewer/Program.cs`](PdfAutoViewer/Program.cs) (línea 39), **cada vez que la app arranca** |
| Clave del registro | `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` |
| Nombre del valor | `PdfAutoViewer` |
| Dato | Ruta completa del `.exe` entre comillas, p. ej. `"C:\Apps\PdfAutoViewer.exe"` |
| Log si falla | `%LOCALAPPDATA%\PdfAutoViewer\app-error.log`, líneas con `[Startup]` |

**Funcionamiento:**
1. Al abrir la app, se escribe (o reescribe) la clave `Run` del **usuario actual**. No necesita admin.
2. Al iniciar sesión, **el Explorador de Windows** lee esa clave y lanza la app.
3. La app es de **instancia única por sesión**. Si se lanza dos veces, la segunda se cierra sola.

**Resultado de la prueba en Wyse:** la app **no** arranca después de reiniciar.

**Ya descartado:**
- El `.exe` persiste tras reiniciar, así que no es que el archivo desaparezca.
- La app se abre desde el Explorador, así que el shell es el normal y la clave `Run` debería procesarse.

**Causas que quedan:**

| # | Causa | Explicación |
|---|---|---|
| 1 | **Write filter (UWF / FBWF)** | Los cambios en disco y registro van a una capa temporal que se **descarta al reiniciar** |
| 2 | **Perfil de usuario no persistente** | El perfil (`HKCU`) se restaura al cerrar sesión |
| 3 | **Inicio desactivado** en "Aplicaciones de inicio" | Desactivado por un usuario o por una política |
| 4 | **Política que impide escribir** en el registro | La app no logra crear la clave |

---

## Parte A: pruebas sin administrador

Todos los comandos se ejecutan en **PowerShell** con el usuario normal de la Wyse.

### A0. Preparación

Define la ruta real del `.exe` en la Wyse. Se usa en varios comandos:

```powershell
$exe = "C:\ruta\real\PdfAutoViewer.exe"   # ← cambiar por la ruta real
Test-Path $exe                            # debe responder True
```

### A1. Después de abrir la app

Abre la app con doble clic en el `.exe`, espera a que aparezca la ventana y ejecuta:

```powershell
# 1) ¿Se escribió la clave de inicio?
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer

# 2) ¿Está desactivada en "Aplicaciones de inicio"? (equivale a lo que muestra el Administrador de tareas)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" /v PdfAutoViewer

# 3) ¿Hubo error al registrar el inicio?
Select-String -Path "$env:LOCALAPPDATA\PdfAutoViewer\app-error.log" -Pattern "Startup"

# 4) ¿El shell es el Explorador? (debe decir explorer.exe)
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell
```

Anota los resultados en la [tabla de registro](#registro-de-resultados).

### A2. Cerrar sesión (sin reiniciar)

1. Cierra sesión en Windows (Inicio → usuario → Cerrar sesión).
2. Vuelve a entrar.
3. Comprueba si la app arrancó sola (icono en la bandeja o ventana visible).
4. Ejecuta:

```powershell
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer
```

### A3. Reiniciar la Wyse

1. Reinicia el equipo.
2. Entra con el mismo usuario.
3. Comprueba si la app arrancó sola.
4. Ejecuta:

```powershell
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer
```

### A4. ¿Está activo el write filter?

Hay tres formas de saberlo sin admin:

**a) Icono en la bandeja.** En muchas Wyse hay un icono de candado o escudo junto al reloj. Al pasar el ratón muestra algo como *"Write Filter Enabled"*.

**b) Prueba con un archivo**, la más fiable:

```powershell
# Antes de reiniciar
"prueba write filter $(Get-Date)" | Out-File "$env:USERPROFILE\Documents\wf-test.txt"

# Después de reiniciar
Test-Path "$env:USERPROFILE\Documents\wf-test.txt"   # False = el filtro descartó el archivo
```

**c) Servicio o driver del filtro** (solo lectura). Si alguno responde, el filtro está instalado; eso no significa necesariamente que esté activo:

```powershell
sc.exe query uwfvol      # Unified Write Filter
sc.exe query fbwf        # File-Based Write Filter (imágenes Wyse antiguas)
```

### A5. Prueba alternativa: carpeta Inicio del usuario

Sirve para comprobar si **cualquier** cambio del perfil se pierde, no solo el registro:

```powershell
# Abre la carpeta Inicio del usuario
explorer.exe shell:startup
```

1. Crea ahí un **acceso directo** al `.exe` (clic derecho → Nuevo → Acceso directo).
2. Reinicia la Wyse.
3. ¿Sigue el acceso directo? ¿Arrancó la app?
4. **Al terminar, borra el acceso directo** para que la app no arranque dos veces. La instancia única lo evita, pero es mejor dejarlo limpio.

### A6. Interpretación

| Resultado | Causa | Siguiente paso |
|---|---|---|
| A1-1 **no** muestra la clave y A1-3 muestra un error `[Startup]` | Política que impide escribir en el registro | Enviar el error a TI → Parte B, solución B3 |
| A1-1 muestra la clave pero **desaparece en A2** | **Perfil no persistente** | TI → solución B3 (registro en `HKLM`) |
| La clave sigue en A2 y **desaparece en A3** | **Write filter** | TI → solución B2 o B3 |
| A4-b devuelve `False` | Write filter activo, confirmado | TI → solución B2 o B3 |
| La clave sigue en A3 y A1-2 empieza por `03` | **Inicio desactivado** en Aplicaciones de inicio | TI → B1-4 |
| La clave sigue en A3, A1-2 no existe o empieza por `02`, y la app no arranca | Otro motivo (política de inicio, antivirus) | Enviar resultados y log a desarrollo |
| A1-4 no dice `explorer.exe` | Shell personalizado: la clave `Run` no se procesa | TI → solución B4 (tarea programada) |

---

## Parte B: pruebas y soluciones con administrador

Abre PowerShell **como administrador**.

### B1. Diagnóstico

```powershell
# 1) Estado del Unified Write Filter (sesión actual y siguiente)
uwfmgr get-config

# 2) Exclusiones configuradas en el filtro
uwfmgr file get-exclusions
uwfmgr registry get-exclusions

# 3) Uso del overlay (si está casi lleno, también provoca fallos)
uwfmgr overlay get-consumption

# 4) "Aplicaciones de inicio": abrir el Administrador de tareas → pestaña Inicio
#    Comprobar que "PdfAutoViewer" aparece y está "Habilitado"
taskmgr

# 5) Directivas que bloqueen la clave Run
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer"
#    Buscar: DisableCurrentUserRun / DisableLocalMachineRun = 1

# 6) Wyse antiguas con File-Based Write Filter
fbwfmgr /displayconfig
```

> Las Wyse con imagen de Dell pueden traer accesos directos o utilidades propias para el filtro (p. ej. "Disable Write Filter" en el escritorio del administrador). Su efecto es el mismo que `uwfmgr filter disable`.

### B2. Solución: registrar con el write filter desactivado

Graba la configuración actual (`HKCU` del usuario) en la imagen de forma permanente.

```powershell
# 1) Desactivar el filtro (aplica en el próximo reinicio)
uwfmgr filter disable
Restart-Computer
```

Después del reinicio, **iniciar sesión con el usuario operador** (el que usará la app):

1. Abrir la app una vez. Se registra sola en `HKCU\...\Run`.
2. Verificar:

```powershell
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer
```

3. Volver a activar el filtro como administrador:

```powershell
uwfmgr filter enable
Restart-Computer
```

4. Repetir la prueba A3. La app debe arrancar sola.

**Limitación:** se registra solo para **ese** usuario. Si la Wyse usa varios usuarios, o el perfil se restaura, conviene más la B3.

### B3. Solución recomendada: registro para todos los usuarios (`HKLM`)

No depende del perfil de cada usuario. Hay que hacerlo **con el filtro desactivado** (pasos 1 y 3 de B2) para que persista.

```powershell
# Ruta fija y protegida para el ejecutable (recomendado)
New-Item -ItemType Directory -Force "C:\Program Files\PdfAutoViewer" | Out-Null
Copy-Item $exe "C:\Program Files\PdfAutoViewer\PdfAutoViewer.exe" -Force

# Registro de inicio para TODOS los usuarios
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer /t REG_SZ /d "\"C:\Program Files\PdfAutoViewer\PdfAutoViewer.exe\"" /f

# Verificar
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer
```

**Notas:**
- La app seguirá escribiendo también su entrada en `HKCU`. No pasa nada: si se lanza dos veces, la **instancia única** cierra la segunda.
- Alternativa equivalente: un acceso directo en la carpeta Inicio común `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp`.
- En despliegues masivos, lo ideal es que TI lo haga con **Wyse Management Suite**, aplicando este registro a todas las Wyse.

### B4. Solución alternativa: tarea programada al iniciar sesión

Útil si el shell no es el Explorador o si la clave `Run` está bloqueada por directiva. También con el filtro desactivado:

```powershell
schtasks /Create /TN "PdfAutoViewer" /TR "\"C:\Program Files\PdfAutoViewer\PdfAutoViewer.exe\"" /SC ONLOGON /RL LIMITED /F

# Verificar
schtasks /Query /TN "PdfAutoViewer" /V /FO LIST
```

### B5. Deshacer o limpiar

```powershell
# Quitar el inicio del usuario actual (sin admin)
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer /f

# Quitar el inicio para todos (admin)
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v PdfAutoViewer /f

# Quitar la tarea programada (admin)
schtasks /Delete /TN "PdfAutoViewer" /F
```

> Si solo se borra la entrada de `HKCU` y luego alguien abre la app a mano, **se vuelve a crear** (la app se registra en cada arranque). Para desactivarlo de forma permanente, usar Configuración → Aplicaciones → Inicio.

> Con el write filter activo, estos cambios también se pierden al reiniciar. Hay que desactivarlo antes.

---

## 2. Casos de prueba manuales (validación final)

Ejecutar después de aplicar la solución.

| ID | Caso | Pasos | Resultado esperado | Pass / Fail |
|---|---|---|---|---|
| ST-01 | Inicio tras cerrar sesión | Cerrar sesión y volver a entrar | La app arranca sola (icono en bandeja + ventana) | ☐ / ☐ |
| ST-02 | Inicio tras reinicio | Reiniciar la Wyse y entrar | La app arranca sola | ☐ / ☐ |
| ST-03 | Persistencia del registro | Tras ST-02, ejecutar `reg query` (HKCU o HKLM según la solución) | La clave existe | ☐ / ☐ |
| ST-04 | Sin duplicados | Tras ST-02, abrir el `.exe` a mano | No aparece una segunda instancia (un solo icono) | ☐ / ☐ |
| ST-05 | Funcionalidad tras inicio automático | Tras ST-02, descargar un PDF | El PDF se abre en el visor y se borra al cerrarlo | ☐ / ☐ |
| ST-06 | Varios reinicios | Repetir ST-02 tres veces | Arranca en todos | ☐ / ☐ |
| ST-07 | Write filter reactivado | `uwfmgr get-config` (admin) | Filtro **activo** en la sesión actual y la siguiente | ☐ / ☐ |
| ST-08 | Otro usuario (si aplica) | Entrar con otro usuario de la Wyse | Arranca (solo con la solución B3/B4) | ☐ / ☐ |

---

## Registro de resultados

Copiar y rellenar por cada Wyse probada.

```
Wyse (nombre / serie):
Usuario:
Ruta del .exe:
Fecha:

A1-1 Clave Run (tras abrir la app):      [ existe / no existe ]
A1-2 StartupApproved:                    [ no existe / 02… / 03… ]
A1-3 Errores [Startup] en el log:        [ ninguno / texto: ... ]
A1-4 Shell:                              [ explorer.exe / otro: ... ]
A2   Clave tras cerrar sesión:           [ existe / no existe ]   App arrancó: [ sí / no ]
A3   Clave tras reiniciar:               [ existe / no existe ]   App arrancó: [ sí / no ]
A4-a Icono write filter:                 [ activo / inactivo / no hay ]
A4-b Archivo de prueba tras reiniciar:   [ True / False ]
A5   Acceso directo en shell:startup:    [ persiste / desaparece ]

Conclusión (causa):
Solución aplicada (B2 / B3 / B4):
Resultado ST-01…ST-08:
```
