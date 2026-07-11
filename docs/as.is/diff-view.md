# Diff view

Wahlberg can compare the active document against another open document or a picked file, and shows the result as a normal tab.

## Key files

- `wahlberg.maui/Services/DiffService.cs` — wraps DiffPlex. Produces a line-based unified HTML view (`BuildUnifiedHtml`, via `InlineDiffBuilder.Diff`), a line-based side-by-side HTML view (`BuildSideBySideHtml`, via `SideBySideDiffBuilder.Diff`), a plain-text unified diff (`BuildUnifiedDiffText`, used by Save Diff), and block-level rendered-markdown diffs (`BuildRenderedUnifiedHtml`/`BuildRenderedSideBySideHtml`, via `Differ.CreateCustomDiffs` diffing each document's top-level Markdig blocks, then re-rendering each block's own HTML).
- `wahlberg.maui/Services/TabService.cs` — `AddDiffDocument(...)` creates a "virtual" tab (`MarkdownDocument.IsDiff = true`) that isn't backed by a real file path and is excluded from `session.json` persistence.
- `wahlberg.maui/Components/DiffPickerPanel.razor` — "Compare With" modal: pick another open document or browse for a file (which also opens it as its own tab).
- `wahlberg.maui/Components/Pages/Home.razor` — Diff toolbar button (hidden unless the active tab is a real, non-diff document), diff tab rendering, unified/side-by-side toggle (`ToggleDiffLayout`, state stored per-tab on `MarkdownDocument.DiffShowSideBySide`), Save Diff button.
- The layout toggle is icon-only (`bi-square` when the tooltip reads "Unified", `bi-layout-split` when it reads "Side-by-side") — the visible label was dropped in favor of a `title` tooltip to keep the diff toolbar compact.
- Diff content has three mutually-exclusive modes, tracked on `MarkdownDocument.DiffMode` (`DiffViewMode.Rendered | Highlighted | Raw`): **Rendered** (each changed block re-rendered through Markdig, block-level diff via `DiffService.BuildRenderedUnifiedHtml`/`BuildRenderedSideBySideHtml`), **Highlighted** (the original colored raw-source line diff), and **Raw** (the plain unified-diff text, the same `Content` already generated for Save Diff, shown in a `<pre class="diff-plain-text">`). `ToggleDiffStyle` cycles only between Rendered ↔ Highlighted; `ToggleDiffRaw` independently toggles Raw on/off, remembering the prior Rendered/Highlighted choice on `MarkdownDocument.DiffNonRawMode` so it can restore it. The layout toggle is hidden while Raw is active, since layout has no meaning for raw text.

## Non-obvious behavior

- `SideBySideDiffBuilder.Diff` emits `ChangeType.Modified` for a line that exists on both sides but differs (the common case for edited/reworded lines), whereas `InlineDiffBuilder.Diff` (used for the unified view) only ever emits `Inserted`/`Deleted`/`Unchanged`. `DiffService.RenderSideLines` must map `Modified` to the same "removed"/"added" CSS classes as `Deleted`/`Inserted` per side, or edited lines silently render as unchanged (no color) in side-by-side mode while still coloring correctly in unified mode.
- Diff tabs are deliberately excluded from `TabService`'s persisted session (`SaveSessionAsync` filters `!d.IsDiff`) since they're derived, not real files — closing and relaunching the app won't restore a diff tab.
