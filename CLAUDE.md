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

# Publish Windows MSIX / self-contained
dotnet publish wahlberg.maui/Wahlberg.Maui.csproj -f net10.0-windows10.0.19041.0 -r win-x64 -c Release --self-contained true -o publish/windows
```

**Prerequisites:** .NET 10 SDK + MAUI workload (`dotnet workload install maui`). No npm or external package managers — all dependencies are NuGet only.

There are no automated tests or lint commands.

In debug builds, `AddBlazorWebViewDeveloperTools()` is registered, so you can right-click inside the WebView to inspect elements and use the browser console.

## Architecture

Wahlberg is a cross-platform Markdown viewer built with .NET MAUI Blazor Hybrid. Three distinct layers:

**MAUI Shell** (`MainPage.xaml.cs`) hosts a `BlazorWebView`. On Windows it maps every local drive letter to a virtual hostname during `BlazorWebViewInitialized` so WebView2 can load local images (`localfile-c`, `localfile-d`, etc.), and it handles native drag-and-drop via MAUI's `DropGestureRecognizer`.

**Blazor UI** (`Components/Pages/Home.razor`, `SettingsPanel.razor`) runs inside that WebView. Components subscribe to service `Action?` events (`StateChanged`, `ThemeChanged`) and call `InvokeAsync(StateHasChanged)` — no Flux/Redux. `IJSRuntime` and a `DotNetObjectReference<Home>` pass into JavaScript for two-way interop.

**Service Layer** (`Services/`) is registered as singletons in `MauiProgram.cs`:

- `TabService` — opens files, converts Markdown to HTML via Markdig (`UseAdvancedExtensions()`), extracts headings with regex, rewrites relative image `src` paths for WebView2, persists open tabs to `session.json`. Heavy work runs in `Task.Run` so the UI shows a loading spinner immediately.
- `ThemeService` — loads/saves `ViewerTheme` objects as JSON files in `FileSystem.AppDataDirectory/themes/`. Active theme name persists to `settings.json`.
- `EditorService` — manages the configured external editor path (persisted to `editor.json`) and maps executable names to icon CSS classes.

**JavaScript** (`wwwroot/js/app.js`) handles everything requiring direct DOM access: injecting theme CSS variables, scroll-position tracking for TOC highlighting (calling back via `SetActiveHeading`), link interception, and rendering Mermaid diagrams. No npm/bundler — libraries are vendored directly into `wwwroot`.

## Windows-specific: single-instance & IPC

`Platforms/Windows/App.xaml.cs` enforces a single running instance via a named Mutex (`WahlbergSingleInstance_{Username}`). If a second launch occurs, it sends the file path to the running instance over a named pipe (`WahlbergIPC_{Username}`) and exits. The pipe server runs in `Task.Run` for the lifetime of the app.

`FileOpenRequest` (a static class) bridges the pipe server to the Blazor `Home` component. It buffers any file path received before the Blazor event subscriber attaches, then drains the queue when `FileRequested` is subscribed. This handles the race where a file is requested before the UI is ready.

On first launch, `RegisterFileAssociations()` writes `HKCU\Software\Classes` entries for `.md`, `.markdown`, etc. so the OS offers Wahlberg as an opener.

## Windows-specific: service mode (`--serve`)

`Platforms/Windows/App.xaml.cs` checks for `--serve`/`--port` at the very top of `OnLaunched`, before the single-instance mutex — if present, it hands off to `Platforms/Windows/ServiceHost.cs`, which hosts the same Blazor UI over Kestrel/Blazor Server instead of the native window, and never touches the mutex/IPC/window-creation path. Service registrations are shared between the native app and this host via `MauiProgram.RegisterSharedServices()`. UI that depends on a native window (file pickers, PDF/Mermaid export) checks `Services.AppMode.IsServiceMode` to fall back to a browser-safe alternative or hide itself. See `docs/as.is/service-mode.md` for the full design and known gotchas (webroot path resolution, missing `blazor.web.js` under `Sdk.Razor`).

## File-opening entry points

Four distinct paths all converge on `TabService.AddDocumentAsync()`:

1. **Command line** — `Home.razor.OpenFileFromCommandLineAsync()` reads `args[1]` on startup.
2. **IPC** — named pipe → `FileOpenRequest.Raise()` → `FileOpenRequest.FileRequested` event → `Home.razor.OnFileRequestedFromIpc()`, which also brings the window to the foreground on Windows.
3. **File picker** — `FilePicker.Default.PickAsync()` in `OpenFile()`.
4. **Drag-and-drop** — MAUI's native `DropGestureRecognizer` on `MainPage` (Windows only, preserves full file path); a browser-side JS drop handler exists as a fallback but only receives the filename without the path.

## Key Conventions

**Local image resolution:** `TabService.ResolveLocalPaths()` rewrites `<img src="./relative.png">` to `https://localfile-c/absolute/path.png` after conversion. The virtual host mappings are registered per drive in `MainPage.xaml.cs` during `BlazorWebViewInitialized`.

**CSS scoping:** `.markdown-body` and all content styling live in `wwwroot/app.css` (global), not in `.razor.css` files. Blazor scoped CSS does not apply to `@((MarkupString)...)` injected HTML. Component-level chrome goes in `.razor.css`.

**`processContent` timing:** `OnAfterRenderAsync` in `Home.razor` only calls `appInterop.processContent` (scroll tracking, link handling, Mermaid) when `!doc.IsLoading && doc.Id != _lastProcessedDocId`. The loading state renders a spinner without a `.document-content` element, so JS setup is deferred until the actual content render.

**Mermaid rendering:** Markdig's `UseAdvancedExtensions()` emits fenced Mermaid blocks as `<pre class="mermaid">`. JS hides the `<pre>`, injects a `<div class="mermaid-rendered">` alongside it, and calls `mermaid.run()`. On document change, `processContent` removes those injected divs and resets `data-mermaid-processed` before re-running.

**Model note:** `Models/AppTheme.cs` contains a class named `ViewerTheme` — the file name and class name differ.

**Multi-targeting:** The single `.csproj` targets `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, and `net10.0-windows10.0.19041.0`. Platform-specific code lives under `Platforms/`. Use `#if WINDOWS` guards for Windows-only WebView2 and WinUI APIs.
