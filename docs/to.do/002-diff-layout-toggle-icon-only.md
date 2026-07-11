# Make the diff unified/side-by-side toggle icon-only

## Problem

The diff tab's layout-toggle button (in `Home.razor`, inside the `.diff-toolbar-actions` block, wired to `ToggleDiffLayout`) currently renders both an icon and a text label:

```razor
<button class="btn-secondary" @onclick="ToggleDiffLayout" title="Toggle layout">
    <i class="bi bi-layout-split"></i> @(TabService.ActiveDocument.DiffShowSideBySide ? "Unified" : "Side-by-side")
</button>
```

It always uses the same `bi-layout-split` icon regardless of which mode is active or about to be switched to, and the visible text makes the toolbar busier than it needs to be.

## Required change

- Remove the visible text label. Put it in the button's `title` attribute instead, so it still shows as a tooltip on hover.
- Swap the icon based on which label the button currently shows (the label reflects the *destination* mode you'd switch to, per the existing `DiffShowSideBySide` ternary — don't change that logic, just make the icon track it):
  - When the label is **"Unified"** (i.e. `DiffShowSideBySide == true`, so clicking switches to unified) → use `bi-square` (plain, undivided square).
  - When the label is **"Side-by-side"** (i.e. `DiffShowSideBySide == false`) → use `bi-layout-split` (already used today — keep it for this case).

Example shape:

```razor
<button class="btn-secondary" @onclick="ToggleDiffLayout"
         title="@(TabService.ActiveDocument.DiffShowSideBySide ? "Unified" : "Side-by-side")">
    <i class="bi @(TabService.ActiveDocument.DiffShowSideBySide ? "bi-square" : "bi-layout-split")"></i>
</button>
```

(`ToggleDiffLayout` itself in the `@code` block doesn't need to change.)

## Verification

1. Open a diff tab (compare two markdown files).
2. Confirm the layout-toggle button shows only an icon, no visible text.
3. Click it — confirm the icon changes (square ↔ split) each time, and hovering over the button shows a tooltip naming the mode you'd switch to.
