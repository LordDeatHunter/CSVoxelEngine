# Inventory UI cleanup
> Guide: plans/README.md

## Status
Design settled with the user on 2026-11-07; nothing implemented. Tracks the visual and UX cleanup of the list-based
inventory UIs: the [E] panel and craft card, tooltips and toasts, and the spell builder. The planned gear and chest
panels (`plans/INVENTORY.md`) are built on the kit from I-U1. One phase per prompt: implement it, update this file,
stop.

**Hard rule:** the shared-count / count-capacity + list-based inventory stays. No grid UI. The hotbar is out of scope.

## In progress
Nothing.

## Open

### Phase 1: kit and tooling
- [ ] **`U-1` Theme, text fitting, widget layouts and scenario ops** - P1, L. Polish the current look (same palette and
  font), do not redesign.
  - [ ] `UiTheme`: spacing scale 4/8/12/16, border widths, colors, text size scale (caption, body, title, head) on
    kongtext with consistent line heights and one shared vertical-centering rule (replaces the hand-tuned `-9/-8`)
  - [ ] text fitting and wrapping helpers: wrap wherever possible, shrink then ellipsis for single-line rows with the
    full text in the hover tooltip as last resort; remove the silent 64-glyph cap (`BitmapFont.MaxLayoutChars`)
  - [ ] GL-free widget layouts: Panel frame, Button (states), Tab strip, ListRow, TextBox, ScrollView, Tooltip;
    renderers only draw
  - [ ] fix `UIRenderer.DrawBorder` (the right edge is drawn about 2px outside the box; confirm visually first)
  - [ ] scenario ops: open the [E] panel, pick a tab, type a search, select a row, hover a point, open the spell
    builder; a baseline scenario in `visual-examples/`
  **Hook:** `UIRenderer`, `BitmapFont`, `TextWrap`, `UiCanvas`, `VisualDirector`, `HudConfig`.

### Phase 2: [E] panel two-column rework
- [ ] **`U-2` List left, detail card right, with states and smooth motion** - P1, L.
  - [ ] one right-hand detail card (item preview now; craft card, equipment and chest side panels later) on one shared
    card frame; positions come from layout classes (retire `PreviewRight = 300`, the `+12f` catalog count inset, tab
    text offsets `+6/+30`, `RowNameAvail`)
  - [ ] main panel gets the same frame treatment as the cards (it has no border or accent bar today)
  - [ ] hover on rows, tabs and buttons; pressed and disabled states; all eased through `MathUtils` / `Spring`
  - [ ] tab underline slide, row selection fade, eased wheel scroll and scroll-to-selection (a newly selected or
    searched row stays in view)
  - [ ] tab hit-testing matches the draw rects (it ignores the 4px gap today); fit tab subtitles and row counts
  **Hook:** `InventoryPanelLayout`, `InventoryPanelRenderer`, `InventoryPanelInput`, `PanelTransition`, `ScrollList`.

### Phase 3: craft card, tooltip, toast
- [ ] **`U-3` Craft card, tooltip and toast on the kit** - P1, M.
  - [ ] craft card onto the card frame; move the renderer-side geometry (`fedCx` offsets, icon sizes, step boxes) into
    `CraftCardLayout`; fit the fed-text and max-batch labels
  - [ ] tooltip wraps to a max width and clamps by line count; long crucible and "Teaches:" lines no longer run off
    the edge
  - [ ] toast gets a panel treatment
  **Hook:** `CraftCardLayout`, `CraftCardRenderer`, `ItemTooltip`, `HudRenderer.DrawTooltip`, `HudToast`.

### Phase 4: spell builder and shared frame
- [ ] **`U-4` Spell builder migration and the frame for gear and chest panels** - P2, M.
  - [ ] spell builder onto the kit; part columns scroll (6 rows max today)
  - [ ] expose the card frame and list-row widgets the equipment panel and chest panel (`I-1`, `I-4`) will use
  **Hook:** `SpellBuilderLayout`, `SpellBuilderRenderer`.

## Reference
- **Decisions.** Animation is tasteful micro-interactions only. Keyboard navigation is not in scope. Verification is
  scenario ops plus GL-free layout and fit tests (a standalone UI preview tool was ruled out as more work than the
  scenario ops, which use the real renderer).
- **Order.** The kit and [E] rework come before the gear and chest panels; `INVENTORY.md` phases reference the kit.
- **Defects found (2026-11-07 survey).**
  - Panel framing is inconsistent: the main panel has no border or accent bar, the preview, craft card and spell
    builder have a border plus a 2px gold bar, and border widths vary 1.5/2/2.5/3.
  - Row, tab and craft-card geometry magic numbers live in the renderer, duplicated with the layout.
  - Unfitted text: preview-card secondary lines, search input text, tab subtitles and tooltip lines.
  - No hover, pressed or focus states; selection, tab switches and scrolling are instant.
  - The spell builder part list has no scroll.
- **Test gap.** The renderers need GL and a font, so new geometry goes in the layout classes to stay testable. The
  only inventory scenario today is `visual-examples/hotbar.json`; the panel, tooltip and spell builder cannot be
  filmed until the U-1 ops exist.
- **Verification.** `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` for layout and fit rules (no
  text overflow at the minimum panel width, wrapped heights, hit-tests equal draw rects); scenarios with
  `VOXEL_TIME=147` at several window sizes; update AGENTS.md per phase.
