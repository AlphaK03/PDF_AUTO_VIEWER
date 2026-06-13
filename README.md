# PDF Auto Viewer

Aplicación de escritorio para Windows que vigila la carpeta de descargas y abre
automáticamente las instrucciones de trabajo (WI / MPI) en formato PDF en un
visor integrado. Al cerrar el documento lo elimina de forma segura para mantener
limpia la carpeta.

Pensada para entornos de manufactura donde los documentos pueden venir en dos
idiomas, identificados por un sufijo en el nombre del archivo (`_SPA`, `_ENG`).

## Comportamiento

- Los PDF se abren siempre en el **visor integrado** (motor WebView2 / Chromium).
- Los PDF se **eliminan automáticamente** al cerrar el documento.
- **Límite de visualización de 20 minutos**: a los 15 minutos se avisa al usuario
  y a los 20 el documento se cierra solo.
- Cuando se descargan ambos idiomas a la vez, se abre el **idioma preferido**;
  si solo llega uno, se abre ese. El idioma preferido es la única opción
  configurable y se elige desde la ventana principal.
- Si se descarga una **copia más reciente** del documento abierto, se reemplaza
  por la versión nueva y se reinicia el límite de tiempo.

## Operación continua

Pensada para ejecutarse de forma desatendida (24/7):

- **Instancia única por sesión de Windows**: evita copias duplicadas dentro de
  una misma sesión, sin bloquear otras sesiones (VDI / multisesión).
- **Registro de errores**: las excepciones no controladas se escriben en
  `%LOCALAPPDATA%\PdfAutoViewer\app-error.log`; un error puntual no detiene la
  aplicación.
- Uso de memoria acotado en ejecución prolongada.

## Requisitos

- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (incluido por defecto en Windows 11)

## Ejecutar la aplicación

```bash
dotnet run --project PdfAutoViewer/PdfAutoViewer.csproj
```

## Generar el ejecutable (.exe)

```bash
dotnet publish PdfAutoViewer/PdfAutoViewer.csproj -c Release -r win-x64 --self-contained false -o publish
```

El ejecutable se genera en `publish\PdfAutoViewer.exe` (requiere el runtime de
.NET 8 en el equipo destino). El script `publish.bat` ejecuta este mismo comando.

También puede abrirse `PdfAutoViewer.sln` directamente en Visual Studio 2022 o
JetBrains Rider.

## Consideraciones de despliegue (Windows 10)

El visor integrado depende del **runtime de WebView2**. En Windows 11 viene
preinstalado; en **Windows 10 no está garantizado**, y como la aplicación no
recurre a un visor alternativo, si el runtime falta el documento no se abrirá.
Antes de desplegar en terminales Windows 10 se recomienda:

- **Verificar que el WebView2 Runtime esté instalado**; si no, instalarlo
  (Evergreen Standalone Installer) o empaquetar la versión *Fixed Version* junto
  a la app para no depender del sistema.
- **Publicar self-contained** (incluir el runtime de .NET en el ejecutable) para
  no depender de que .NET 8 esté instalado. Requiere Windows 10 versión 1607 o
  superior.
- Tener presente que **Windows 10 alcanzó su fin de soporte en octubre de 2025**;
  conviene confirmar el plan de actualizaciones (ESU o migración a Windows 11).

## Estructura

```
PdfAutoViewer.sln              Solución de Visual Studio
PdfAutoViewer/
  Program.cs                   Punto de entrada
  PdfAutoViewer.csproj         Proyecto (.NET 8, Windows Forms)
  Core/                        Lógica: configuración, vigilancia, ciclo de vida
  UI/                          Interfaz: bandeja, ventana de estado, visor
```

## Configuración

La única opción configurable es el **idioma preferido**, que se elige desde la
ventana principal de la aplicación y se guarda en
`%LOCALAPPDATA%\PdfAutoViewer\settings.json`. El resto del comportamiento
(carpeta vigilada, visor integrado, auto-eliminación, límite de 20 minutos) es
fijo por diseño.

## Dependencias

| Dependencia | Uso |
|---|---|
| .NET 8 / Windows Forms | Plataforma e interfaz |
| `Microsoft.Web.WebView2` | Visor integrado (motor Chromium PDF) |
