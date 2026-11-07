# Inventory UI cleanup
> Guide: plans/README.md

## Status
Design settled with the user on 2026-11-07; `U-1` (the kit and tooling) is done. Tracks the visual and UX cleanup of the list-based
inventory UIs: the [E] panel and craft card, tooltips and toasts, and the spell builder. The planned gear and chest
panels (`plans/INVENTORY.md`) are built on the kit from I-U1. One phase per prompt: implement it, update this file,
stop.

**Hard rule:** the shared-count / count-capacity + list-based inventory stays. No grid UI. The hotbar is out of scope.

## In progress
Nothing.

## Open

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
  - [ ] the tooltip anchor is the mouse in window pixels but is drawn in canvas units (`UiPasses.RenderTooltip`, `HudRenderer.DrawTooltip`), so it drifts from the cursor whenever the canvas scale is not 1: convert it through `UIRenderer.ToCanvas` and place the box with `TooltipLayout`
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
- **What U-1 shipped.** `UiTheme` (spacing, borders, text sizes `Body`/`Head`, palette; no caption or title sizes until something needs them), `TextFit` and the removal of the 64-glyph cap (the text batch holds 2048 glyphs), `UIRenderer.DrawTextCenteredY`/`DrawTextMiddle` (cap-box centering replaced every hand-tuned `-8/-9`, so one-line labels moved about 2 px down), the `DrawBorder` right-edge fix, `UI/Widgets/` (all seven layouts with tests; `TextBoxLayout` is wired into `DrawInputField`, the rest are first consumed by U-2 to U-4), and the scenario ops (`panel`, `tab`, `search`, `row`, `hover`, `spellbuilder`) with `visual-examples/inventory-ui.json` as the baseline.
- **Verification.** `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release` for layout and fit rules (no
  text overflow at the minimum panel width, wrapped heights, hit-tests equal draw rects); scenarios with
  `VOXEL_TIME=147` at several window sizes; update AGENTS.md per phase.
