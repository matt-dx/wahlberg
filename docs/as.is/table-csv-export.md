# Export tables to CSV

Every rendered Markdown table gets a small icon-only "Export as CSV" button that saves that
table's data as a standalone `.csv` file.

## Key files

- `wahlberg.maui/wwwroot/js/app.js` — `_injectTableExportButtons()` (called from `processContent`,
  same hook that drives Mermaid/diff-syntax rendering) scans `.document-content` (the plain,
  non-diff container) for `table` elements not yet marked `data-csv-export-processed`, and inserts
  a `.table-export-toolbar` bar with one button directly above each. `_tableToCsv(table)` builds
  RFC-4180-style CSV from the table's `tr`/`th`/`td` cells (quotes/commas/newlines are quoted and
  escaped; `<br>` is swapped for a literal `\n` on a cloned cell first, since `textContent` drops
  `<br>` entirely). The click handler resolves the table's index among all tables in the container
  at click time (not at injection time), asks .NET for the export filename/mode via
  `GetTableCsvExportInfo(tableIndex)`, and then either downloads the CSV as a browser Blob directly
  (service mode) or hands it to .NET's `SaveTableCsv` (native mode).
- `wahlberg.maui/Components/Pages/Home.razor` — `GetTableCsvExportInfo(int tableIndex)` returns a
  `TableCsvExportInfo(FileName, IsServiceMode)` record (`{document-name}-table-{n}.csv` +
  `AppMode.IsServiceMode`) — a tiny payload, not the CSV itself. `SaveTableCsv(string csv, string
  fileName)` is native-only and calls `FileSaver.Default.SaveAsync`, mirroring `SaveDiff`'s native
  branch.
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
- Unlike `SaveDiff` (which streams .NET-generated content *to* JS to dodge SignalR's message-size
  limit), the risk here runs the other way: a large CSV built in JS being sent *to* .NET as a
  single `invokeMethodAsync` argument could blow the same limit in service mode. So the CSV never
  leaves the browser in service mode — only the small filename/mode metadata round-trips through
  .NET, and the Blob download happens entirely in JS. The native path still hands the CSV to .NET
  because `FileSaver.Default.SaveAsync` requires it; that call goes over the local BlazorWebView
  interop channel, not SignalR, so it isn't subject to the same limit.
- Verified manually in `--serve` mode: comma/quote-containing cells and a `<br>`-separated cell
  round-tripped correctly through the Blob-download path. The native `FileSaver` path wasn't
  independently re-verified — same `SaveTableCsv` code as already proven by Save Diff's identical
  branch.
