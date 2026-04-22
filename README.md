# Wahlberg

A cross-platform Markdown viewer built with .NET MAUI and Blazor.

## Features

- **Tabbed viewing** — open multiple Markdown files at once and switch between them
- **Table of contents** — automatically generated from document headings, with active heading tracking as you scroll
- **Themes** — customizable viewer themes with support for creating, editing, importing, and exporting theme JSON files
- **Session restore** — reopens your previously open files on next launch
- **Drag and drop** — drop a Markdown file onto the window to open it
- **Local image support** — relative image paths in documents resolve to local files

## Supported Platforms

| Platform | Minimum Version |
|----------|---------------- |
| Windows  | 10.0.17763.0    |
| Android  | API 24          |
| iOS      | 15.0            |
| macOS    | 15.0            |

## Tech Stack

- [.NET 10](https://dotnet.microsoft.com/) — MAUI + Blazor Hybrid
- [Markdig](https://github.com/xoofx/markdig) — Markdown parsing and HTML rendering
- [CommunityToolkit.Maui](https://github.com/CommunityToolkit/Maui) — file picker and platform helpers

## Installation

### Microsoft Store

Search for **Wahlberg** in the Microsoft Store, or install directly:

[![Get it from Microsoft](https://get.microsoft.com/images/en-us%20dark.svg)](https://www.microsoft.com/store/apps/9PN7LZ0ZNX9X)

### winget

```bash
winget install MattWhitwam.Wahlberg
```

or

```bash
winget install --id 9PN7LZ0ZNX9X -s msstore
```

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022+ with the **.NET MAUI** workload, or the MAUI workload installed via `dotnet workload install maui`

### Build and Run

```bash
# Windows
dotnet run --project wahlberg.maui -f net10.0-windows10.0.19041.0

# Android (device or emulator required)
dotnet run --project wahlberg.maui -f net10.0-android
```

Or open `Wahlberg.slnx` in Visual Studio and press **F5**.

## Project Structure

```text
wahlberg.maui/
├── Components/
│   ├── Layout/          # Shell layout components
│   ├── Pages/           # Blazor pages (Home)
│   ├── Routes.razor
│   └── SettingsPanel.razor
├── Models/              # MarkdownDocument, HeadingInfo, AppTheme, TabOrientation
├── Services/
│   ├── TabService.cs    # Document/tab management and session persistence
│   └── ThemeService.cs  # Theme loading, saving, and switching
├── wwwroot/             # Static assets and JavaScript interop
└── MauiProgram.cs       # App bootstrap
```
