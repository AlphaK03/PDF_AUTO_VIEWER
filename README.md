# PDF Auto Viewer

Aplicación de escritorio para Windows que vigila una carpeta de descargas y abre
automáticamente las instrucciones de trabajo (WI / MPI) en formato PDF. Al cerrar
el documento puede eliminarlo de forma segura para mantener limpia la carpeta.

Pensada para entornos de manufactura donde los documentos pueden venir en dos
idiomas, identificados por un sufijo en el nombre del archivo (`_SPA`, `_ENG`).

## Requisitos

- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft Edge (incluido en Windows)
- WebView2 Runtime — necesario solo si se usa el visor integrado
  (incluido por defecto en Windows 11)

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

La configuración del usuario se guarda en
`%LOCALAPPDATA%\PdfAutoViewer\settings.json` y se edita desde el diálogo de
ajustes de la propia aplicación (idioma preferido, carpeta vigilada, eliminación
automática, visor integrado, etc.).

## Dependencias

| Dependencia | Uso |
|---|---|
| .NET 8 / Windows Forms | Plataforma e interfaz |
| `Microsoft.Web.WebView2` | Visor integrado (motor Chromium PDF) |
