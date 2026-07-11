# Fix missing +/- colors in side-by-side diff view

## Problem

In the diff comparison tab, the **unified** layout correctly highlights added lines in green and removed lines in red. The **side-by-side** layout does not — on real-world documents (where lines are edited/reworded rather than purely added or removed), the side-by-side view shows no coloring at all, even though the same underlying diff is colored correctly in unified mode.

## Root cause

`DiffService.RenderSideLines` (`wahlberg.maui/Services/DiffService.cs`) maps each line's `ChangeType` to a CSS class:

```csharp
var cssClass = line.Type switch
{
    ChangeType.Deleted when isOldSide => "diff-line-removed",
    ChangeType.Inserted when !isOldSide => "diff-line-added",
    ChangeType.Imaginary => "diff-line-imaginary",
    _ => "diff-line-unchanged"
};
```

`SideBySideDiffBuilder.Diff` (used for the side-by-side view) emits `ChangeType.Modified` for a line that exists on both sides but differs — this is the common case for real documents where a line is edited rather than purely inserted or deleted. `InlineDiffBuilder.Diff` (used for the unified view) has no `Modified` type — it only ever emits `Inserted`/`Deleted`/`Unchanged` — which is why unified mode already colors correctly.

Because `Modified` isn't handled in the switch above, it falls through to `_ => "diff-line-unchanged"`, so any edited line renders with no color in side-by-side mode.

## Required change

Extend the switch in `RenderSideLines` so `ChangeType.Modified` is treated the same as `Deleted` on the old side and `Inserted` on the new side:

```csharp
var cssClass = line.Type switch
{
    ChangeType.Deleted or ChangeType.Modified when isOldSide => "diff-line-removed",
    ChangeType.Inserted or ChangeType.Modified when !isOldSide => "diff-line-added",
    ChangeType.Imaginary => "diff-line-imaginary",
    _ => "diff-line-unchanged"
};
```

No other files need to change for this fix — the CSS classes (`diff-line-removed`, `diff-line-added`) already exist in `wahlberg.maui/wwwroot/app.css`.

## Verification

1. Open two markdown files that differ by edited lines (not just pure additions/removals — e.g. a heading or sentence reworded, not just appended).
2. Click Diff, compare the two files.
3. Confirm the unified view colors the changed lines (this already works).
4. Toggle to side-by-side and confirm the same changed lines now show red (old side) / green (new side) highlighting, matching what unified mode shows for the same diff.
