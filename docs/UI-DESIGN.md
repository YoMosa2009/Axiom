# Axiom interface direction

The interface should feel like a quiet technical notebook: warm, legible, and useful under sustained work. Preserve Axiom's charcoal, parchment, and gold palette and the existing feature names.

Since V1.9.1 that palette is one theme among several rather than a set of fixed hex values. Colours come from the named roles in `AppTheme` — background, surface, border, text, accent, and so on — which every theme fills in. Write new UI against those roles via `DynamicResource`, never against a literal hex, so a theme change repaints it. Axiom Dark remains the default and the look this document describes; "gold" below means the accent role, which another theme is free to render differently.

## References

- [Geist's Anthropic identity case study](https://geist.co/work/anthropic): the studio describes pairing technical craft with human character, combining sans and serif typography, and creating a warm color system for both marketing and product UI. Axiom applies that general approach through its existing Georgia/Segoe UI pairing and its own colors.
- [Claude's interface guidelines](https://claude.com/docs/connectors/building/mcp-apps/design-guidelines): a small type hierarchy, restrained visual surfaces, and layouts that adapt to their container. These guidelines concern embedded Claude apps; they are inspiration, not a specification for Axiom's desktop workflows.

## Component rules

- Page and welcome headings: Georgia, regular weight. Controls and body text: Segoe UI. Keep code in the existing monospace editor.
- Use gold fills for primary actions. Pair them with charcoal text. Use tinted surfaces and gold detail for selection, rather than filling every selected navigation item with gold.
- Keep controls geometrically stable when hovered or pressed. Use a quiet overlay instead of scale animations and glowing shadows.
- Standard button radius: 8 DIPs. Fields: 6. Main composer and settings surface: 16. Avoid nesting several strongly outlined cards where spacing is sufficient.
- Respect `Padding`, content alignment, access keys, keyboard focus, and disabled states in every custom template. Keep WPF's required named parts for editors and scrolling.
- Put the Workplace editor above its wrapping action row. The model selectors, effort control, Stop, and Run Council must never consume the editor's width.
- Keep status colors and runtime-controlled visibility intact. Preserve all named controls, event handlers, commands, and bindings.

## Implementation boundary

The redesign changes XAML and presentation-only colors/spacing in MainWindow code. No inference, tool execution, persistence, model routing, or council orchestration logic is changed. Pre-existing uncommitted feature work remains in place.

## Validation

Use an isolated `AXIOM_DATA_DIR` for previews. Build the WPF project and compare named controls, bindings, and event handlers against the pre-edit XAML. Inspect Chat, Workplace with the canvas open and closed, Persona, Neuron, settings, and dialogs. Include a compact window, keyboard focus, a wrapped Workplace toolbar, and scrolling. A successful build is not evidence that model inference or connected services have been exercised.

2026-09-04 verification: Debug build passed with zero errors and 348 existing warnings. The XAML contract comparison preserved named controls, bindings, and event wiring outside templates. An in-process WPF probe loaded the resource dictionary and verified slider track/value synchronization, the IncreaseSmall command, the TextBox editing host, and primary-button foreground. Desktop inspection covered Chat at normal and minimum width, Workplace with canvas expanded/collapsed, Persona, Neuron, and settings. Dialog changes were build-checked. Model inference and external integrations were not exercised.
