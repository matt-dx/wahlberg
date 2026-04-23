# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Run on Windows (primary target)
dotnet run --project wahlberg.maui -f net10.0-windows10.0.19041.0

# Build only
dotnet build wahlberg.maui/wahlberg.maui.csproj -f net10.0-windows10.0.19041.0

# Run on Android (requires connected device or emulator)
dotnet run --project wahlberg.maui -f net10.0-android
```

**Prerequisites:** .NET 10 SDK + MAUI workload (`dotnet workload install maui`). No npm or external package managers — all dependencies are NuGet only.

There are no automated tests or lint commands.

## Architecture

Wahlberg is a cross-platform Markdown viewer built with .NET MAUI Blazor Hybrid. There are three distinct layers:

**MAUI Shell** (`MainPage.xaml.cs`) hosts a `BlazorWebView`. It handles Windows-specific setup: mapping local drive letters to virtual hostnames so WebView2 can load local images (`localfile-c`, `localfile-d`, etc.), and platform native drag-and-drop.

**Blazor UI** (`Components/Pages/Home.razor`, `SettingsPanel.razor`) is a single-page app running inside that WebView. Components subscribe to service events (`StateChanged`, `ThemeChanged`) and call `InvokeAsync(StateHasChanged)` — there is no Flux/Redux-style state management. `IJSRuntime` and a `DotNetObjectReference<Home>` are passed into JavaScript for two-way interop.

**Service Layer** (`Services/`) contains the business logic:

- `TabService` — opens files, converts markdown to HTML via Markdig, extracts headings with regex, rewrites relative image `src` paths for WebView2, persists session state to `FileSystem.AppDataDirectory/session.json`. All heavy work runs on a background thread via `Task.Run` so the UI shows a loading spinner immediately.
- `ThemeService` — loads/saves `ViewerTheme` objects as JSON files in `FileSystem.AppDataDirectory/themes/`.
- `EditorService` — manages the configured external editor path and maps executable names to icon classes.

**JavaScript** (`wwwroot/js/app.js`) handles everything that requires direct DOM access: injecting theme CSS variables, tracking scroll position for TOC highlighting (calling back to Blazor via `SetActiveHeading`), and rendering Mermaid diagrams. No npm/bundler — libraries are vendored directly into `wwwroot`.

## Key Conventions

**Local image resolution:** `TabService.ResolveLocalPaths()` rewrites `<img src="./relative.png">` to `https://localfile-c/absolute/path.png` after conversion. The virtual host mappings are registered per-drive in `MainPage.xaml.cs` during `BlazorWebViewInitialized`.

**CSS scoping:** `.markdown-body` and all content styling live in `wwwroot/app.css` (global), not in `.razor.css` files. This is intentional — Blazor scoped CSS doesn't apply to `@((MarkupString)...)` injected HTML. Component-specific layout/chrome goes in `.razor.css`.

**`processContent` timing:** `OnAfterRenderAsync` in `Home.razor` only calls `appInterop.processContent` (which wires scroll tracking and Mermaid) when `!doc.IsLoading && doc.Id != _lastProcessedDocId`. The loading state renders a spinner without a `.document-content` element, so JS setup must be deferred until the actual content render.

**Model note:** `Models/AppTheme.cs` contains a class named `ViewerTheme` — the file name and class name differ.

**Multi-targeting:** The single `.csproj` targets `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, and `net10.0-windows10.0.19041.0`. Platform-specific code lives under `Platforms/`. Use `#if WINDOWS` guards for Windows-only WebView2 APIs.
