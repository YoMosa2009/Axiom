# UI layout and composer correction — 2026-11-17

The fixed-height custom caption clipped the Workplace header, and the composer
showed too many small controls when panels reduced the chat width.

## Changes

- Restored the OS-owned title bar and native minimize, maximize/restore and close
  buttons. The application toolbar now measures its own height in the client area.
- Workplace header actions can move below the title when the available width is
  insufficient. Changes in visible controls also trigger this layout calculation.
- Message rows follow their composer's width in both normal Chat and Workplace.
  Normal Chat gutters respond to the chat column, including panel/sidebar changes.
- Normal Chat model choices and run controls move into separate aligned rows when
  needed, rather than clipping or breaking individual controls across lines.
- Workplace keeps attachments/tools, project, compact effort and send in the
  composer. Review policy, web search and skills are available in the `+` menu.
  Stop appears when enabled. The existing tool settings and handlers are preserved.
- Review and skills popups use the visible composer menu trigger as their anchor,
  and close the first menu before opening the next panel.

## Validation

- Final WPF Debug build: zero errors; 352 existing warnings.
- Regression suite: 900 passed, zero failed or skipped.
- Isolated WPF render harness: 20 Chat/Workplace width and panel combinations
  passed at requested window widths 1040, 1280 and 1600 DIPs (normal Chat at 1040
  and 1600). Verified matching message/composer widths, visible header button
  bounds and normal Chat toolbar button bounds.
- Menu interaction checks passed: settings hidden while the menu is closed,
  tools visible when opened, review/skills panels open with a visible anchor,
  and Stop visibility follows its enabled state.
- Maximized/restored render checks and native caption configuration passed.
  Native capture showed the standard OS caption buttons; the private desktop's
  GDI capture did not paint the WPF client area, so client visuals were inspected
  separately using WPF rendering.
- All app renders used an isolated profile on a private desktop. No user desktop
  input or model requests were sent.

Local evidence (ignored build artifacts):
`artifacts/layout-composer-menu-final/report.json`, `composer.png`,
`tools-menu.png`, and the corresponding Chat/Workplace renders.

These are local changes, not a published OTA release.
