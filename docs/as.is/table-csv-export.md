# Export tables to CSV

Every rendered Markdown table gets a small icon-only "Export as CSV" button that saves that
table's data as a standalone `.csv` file.

## Key files

- `wahlberg.maui/wwwroot/js/app.js` — `_injectTableExportButtons()` (called from `processContent`,
  same hook that drives Mermaid/diff-syntax rendering) scans `.document-content` (the plain,
  non-diff container) for `table` elements not yet marked `data-csv-export-processed`, and inserts
  a `.table-export-toolbar` bar with one button directly above each. `_tableToCsv(table)` builds
  RFC-4180-style CSV from the table's `tr`/`th`/`td` text content (quotes/commas/newlines are
  quoted and escaped). The click handler resolves the table's index among all tables in the
  container at click time (not at injection time) and invokes `ExportTableAsCsv(csv, tableIndex)`
  via the existing `DotNetObjectReference`.
- `wahlberg.maui/Components/Pages/Home.razor` — `[JSInvokable] ExportTableAsCsv(string csv, int
  tableIndex)` names the file `{document-name}-table-{n}.csv` and saves it via the same dual-path
  as `SaveDiff`: `FileSaver.Default.SaveAsync` (native Save-As dialog) normally, or
  `appInterop.downloadFileFromStream` (browser Blob download) when `AppMode.IsServiceMode`.
- `wahlberg.maui/wwwroot/app.css` — `.table-export-toolbar`/`.table-export-btn` styling, matching
  the existing subtle icon-button look used elsewhere in the toolbar.

## Non-obvious behavior

- The button is injected as a new DOM sibling *before* the `<table>`, not as a wrapper — this
  avoids interfering with the table's `overflow`/sticky-header CSS. The
  `data-csv-export-processed` marker guard follows the same convention as
  `data-mermaid-processed`/`data-diff-processed`: needed because Blazor's diffing leaves the
  existing DOM subtree untouched when the underlying `MarkupString` content is unchanged (e.g. an
  unrelated re-render), so re-running `processContent` without the guard would insert duplicate
  buttons. A genuine document-content change replaces the whole subtree (including any injected
  toolbar), so no extra cleanup is needed on dispose.
- Scoped to `.document-content:not(.diff-content)` — diff-view tables (in the side-by-side/unified
  diff renderer) are out of scope for this feature.
- Verified manually in `--serve` mode via the browser's Blob-download fallback path; the native
  `FileSaver` path (default desktop mode) uses the identical dual-path branch already exercised and
  proven by Save Diff, just with the `AppMode.IsServiceMode` check inverted.
