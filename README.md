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

## Requisitos

- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- WebView2 Runtime (incluido por defecto en Windows 11)

## Compilar y ejecutar

```bash
# Restaurar dependencias y ejecutar
dotnet run --project PdfAutoViewer/PdfAutoViewer.csproj

# Compilar en modo Release
dotnet build -c Release

# Publicar un ejecutable de un solo archivo
dotnet publish PdfAutoViewer/PdfAutoViewer.csproj -c Release -r win-x64 --self-contained false -o publish
```

También puede abrirse `PdfAutoViewer.sln` directamente en Visual Studio 2022 o
JetBrains Rider.

## Estructura

```
PdfAutoViewer.sln              Solución de Visual Studio
PdfAutoViewer/
  Program.cs                   Punto de entrada
  PdfAutoViewer.csproj         Proyecto (.NET 8, Windows Forms)
  Core/                        Lógica: configuración, vigilancia, ciclo de vida
  UI/                          Interfaz: bandeja, estado, configuración, visor
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
