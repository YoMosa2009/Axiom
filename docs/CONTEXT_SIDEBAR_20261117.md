# Context sidebar readability and navigation — 2026-11-17

- Workplace Context/Environment body text is now 13 DIPs in Segoe UI, with
  14-DIP section headings and 19-DIP body line height. Budget rows have larger
  gaps, and section separators, icons, progress bar and compact-session control
  have more room.
- Budget labels wrap at narrow widths. Output filenames use a constrained grid
  column, so long names truncate with their full path available in a tooltip.
- Generated output/background-task rows use the same readable type scale.
  Background task stop controls have a larger 28-DIP target.
- Removed the floating left-sidebar expand arrow. The navigation-bar arrow is the
  single show/hide control; direction, tooltip and accessibility name follow the
  sidebar state. Removed the old header offsets reserved for the floating arrow.

Validation: WPF Debug build passed with zero errors (352 existing warnings).
Private-desktop tests using an isolated profile verified the navigation toggle
in Chat, Workplace and Persona, expanded and collapsed. Context panel rendering
was checked at widths 450, 360 and 290 DIPs and window heights 1000 and 640 DIPs:
13-DIP text, row bounds, wrapping and scrolling to the final background-task
section passed. Row-height assertions allow one physical pixel for DPI rounding.
No model calls or user desktop input were sent.

Evidence is in ignored local `artifacts/context-sidebar-verified/`. Each final
run used a fresh isolated profile and waited for sidebar animations. A fixture tracker file
exercises the long output filename; it is not a real task result.

These changes are local and have not been published as an OTA release.
