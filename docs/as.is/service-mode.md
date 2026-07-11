# Service mode (`--serve`)

`Wahlberg.exe --serve [--port N]` (Windows only, default port `5230`) hosts the existing Blazor UI over Kestrel/Blazor Server at `http://localhost:{port}` instead of opening the native WinUI window. No MAUI `Window` is ever created in this code path. Intended for testing/automation (e.g. driving the app via a browser) without taking over the desktop — it runs alongside an already-open windowed instance, since it uses a separate code path from the existing single-instance mutex/IPC logic entirely.

## Key files

- `wahlberg.maui/Platforms/Windows/App.xaml.cs` — `TryGetServePort()` parses `--serve`/`--port` at the very top of `OnLaunched`, before the mutex/pipe/window-creation logic, and branches off to `ServiceHost.RunAsync(port)` instead.
- `wahlberg.maui/Platforms/Windows/ServiceHost.cs` — builds a `WebApplicationBuilder` anchored to `AppContext.BaseDirectory` (not the process's working directory, which may not contain `wwwroot`), registers the same services as the native app via `MauiProgram.RegisterSharedServices`, and maps `Components/WebHost/App.razor` with `AddInteractiveServerRenderMode()`.
- `wahlberg.maui/Components/WebHost/App.razor` — the ASP.NET Core Blazor root document (distinct from the MAUI head's `wwwroot/index.html`, which boots via `blazor.webview.js` and can't be reused as-is). Reuses `Routes.razor` and the existing static assets (`app.css`, `js/app.js`, `js/mermaid.min.js`) unchanged.
- `wahlberg.maui/Services/AppMode.cs` — `AppMode.IsServiceMode` static flag, checked by UI that depends on a native window.
- `wahlberg.maui/Wahlberg.Maui.csproj` — Windows-only `<FrameworkReference Include="Microsoft.AspNetCore.App" />` brings in Kestrel/ASP.NET Core hosting without requiring `Microsoft.NET.Sdk.Web`.

## Service-mode UI differences

Native-window-dependent features are hidden or replaced rather than left broken:

- **Open** (`Home.razor`) and **Diff → Or Choose a File** (`DiffPickerPanel.razor`): a plain text file-path input replaces `FilePicker.Default.PickAsync`, which throws `InvalidOperationException` with no native window to associate the WinRT picker with.
- **Save Diff**: triggers a client-side download via a new `appInterop.downloadFileFromStream` JS helper (`DotNetStreamReference` read as an `arrayBuffer()`, then Blob + synthetic anchor click) instead of `FileSaver.Default.SaveAsync`. Streamed rather than passed as a single JS-interop string argument so large diffs don't exceed Blazor Server's SignalR message size limit.
- **Export button**: hidden entirely (PDF/Mermaid export depends on `HiddenWebView`, which itself depends on a native window — see the follow-up item below).
- **Settings → External Editor / theme import / theme export**: hidden (editor picker launches a local `.exe` path that's meaningless server-side; theme import/export use `FilePicker`/`FileSaver`).

## Known limitations of `--serve`

- **Local image resolution doesn't work.** `TabService.ResolveLocalPaths`/`DiffService`'s block renderer rewrite relative `<img src>` to `https://localfile-{drive}/...` for WebView2's virtual-host mapping (see `MainPage.xaml.cs`), which only exists in the native windowed app. A regular browser has no such mapping, so these images 404 (and `DiffService` specifically skips the rewrite when `AppMode.IsServiceMode` is set, to avoid generating dead URLs/DNS lookups for the fake hostname — regular document rendering via `TabService.AddDocumentAsync` still emits them unchanged, since that path predates `--serve` and isn't part of this MVP slice).
- **The path-input fallbacks are not sandboxed.** Since there's no native file picker in a browser, `Home.razor`/`DiffPickerPanel.razor` fall back to a plain text path input that's read directly off disk with no authentication or path restriction. Anyone who can reach the `--serve` port can read arbitrary files readable by the account running `Wahlberg.exe`. This mode has no auth story — only run it on a trusted machine/network, e.g. bound to `localhost` for local automation.

## Non-obvious behavior / gotchas hit during implementation

- `WebApplication.CreateBuilder()` resolves `ContentRootPath`/`WebRootPath` relative to the **current working directory**, not the executable's directory — if launched from elsewhere, `wwwroot` isn't found. Fixed by passing `new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory }`.
- `MapRazorComponents<T>().AddInteractiveServerRenderMode()` requires `app.UseAntiforgery()` in the pipeline (between static files and the component mapping) — omitting it throws `InvalidOperationException` on every request.
- **This project uses `Microsoft.NET.Sdk.Razor` with a manual `FrameworkReference`, not `Microsoft.NET.Sdk.Web`.** `Microsoft.NET.Sdk.Web` implicitly wires up the ASP.NET Core "static web assets" pipeline that serves the framework's own `_framework/blazor.web.js`; a bare `FrameworkReference` does not, and MAUI's own `wwwroot` handling is a raw physical copy, not the static-web-assets pipeline either — so `blazor.web.js` 404s by default. Fixed by vendoring the real file directly into `wwwroot/_framework/blazor.web.js` (copied from the `microsoft.aspnetcore.app.internal.assets` NuGet package's own static web assets, matching this project's existing convention of vendoring JS libraries directly into `wwwroot` rather than relying on a bundler).
  - **This is a manually-pinned copy that can silently drift from whatever `Microsoft.AspNetCore.App` version is actually resolved on a given machine** (the `FrameworkReference` here doesn't pin a version, so it rolls forward to the latest installed 10.0.x runtime). There's no reliable build-time way to auto-resolve the exact matching file, since `microsoft.aspnetcore.app.internal.assets` isn't part of this project's own restore graph (confirmed: `ResolvedFrameworkReference` isn't populated for a `Microsoft.NET.Sdk.Razor`-based project the way it would be for `Microsoft.NET.Sdk.Web`). The vendored file's pinned version is tracked in a sibling `wwwroot/_framework/blazor.web.js.version` marker with refresh instructions — check it against the actual runtime's ASP.NET Core version after an SDK upgrade, and re-vendor if they've diverged.
- Session persistence (`TabService`/`session.json`) is shared with the native app — a `--serve` instance run against the same user profile sees (and can affect) the same restored tabs. No special isolation was added for this MVP slice.
