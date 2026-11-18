# Workplace layout, desktop continuation and retained context

## Owner report and observed causes

The 22:23 Computer Use run opened Paint and drew six stickman strokes; controller telemetry
reported 4,516 changed pixels and four desktop steps. At 22:29, the untagged smiley-face
follow-up entered Agent Access because routing required a fresh explicit Computer Use mention.
It searched project files. The subsequent explicitly tagged attempt lacked prior app/task state,
saw ChatGPT in front and clicked an unrelated thumbnail. Neither attempt established a smiley face.

The UI used a separate native caption above the Workplace header, and messages were aligned
to opposite sides of the entire viewport. Collapsing the sidebars magnified that separation.

## Implemented behavior

- A unified 40-pixel caption hosts the existing navigation, Workplace session header and
  controls. WPF WindowChrome supplies native caption dragging/resizing; caption buttons invoke
  SystemCommands. Header controls remain interactive in the caption region.
- The chat and composer use a centered region capped at 960 pixels. Narrow viewports remain
  fluid. Existing user/assistant alignment remains inside that shared region.
- Desktop mode persists through follow-ups without classifying request words. Prior goal,
  result and application are included in the next run, stored per chat and cleared for a new
  session or through the visible desktop-context exit control. Existing tagged snapshots can
  bootstrap the mode. Recent chat is also supplied to desktop planning/action prompts.
- Before its first desktop action, a continuation captures the previous work app directly.
  Missing/minimized apps yield an empty target and an explicit recovery state, instead of
  returning a screenshot/catalog of an unrelated foreground app. Input is refused without a
  target; an explicit open action can restore the intended app.
- Full conversation/tool evidence is retained in profile-local per-chat archives. The
  read-only recall tool supports literal queries and paginated character offsets. Native
  Chat/Council, Agent Access, local council pause tools and desktop actions expose recall.
  Shared MCP/Python hooks and background-output capture preserve evidence before prompt caps.
  Agent screenshots and desktop captures are retained as image evidence for later reference.
- Context compaction remains necessary for finite model windows. The archive is the full
  record; a shortened prompt is not presented as the only available evidence. The previous
  arbitrary 250-character cut on local Agent conversation messages was removed. Records already
  discarded by older builds cannot be recreated from summaries.

## Validation

- Final full suite: 900 passed, zero failed or skipped. A background wake test failed once
  during simultaneous builds, passed in isolation, and passed in the final full run without
  changing or disabling it.
- Debug app build: zero errors; simulation harness build: zero errors.
- Actual MainWindow on a private desktop with a throwaway profile: header moved into caption,
  caption buttons visible, both sidebars collapsed through UI/state controls, conversation width
  960 pixels, left sidebar width zero. Both rendered states were visually inspected.
  Evidence: `artifacts/workplace-layout-check/report.json`, `sidebars-open.png`,
  `sidebars-collapsed.png`. Rendered chat contents are a test fixture, not a live model result.
- Passive missing-Paint check: zero target handle, no unrelated foreground app substituted,
  desktop focus unchanged. Paint was no longer open, so this is missing-app recovery evidence,
  not physical acceptance of a newly drawn smiley face. No mouse or keyboard input was sent.
- Positive preferred-application check selected the available ChatGPT window by process identity
  and left desktop focus unchanged. This tests the generic application selector, not a Paint run.
- No paid model requests were spent and no real-profile UI state was modified by these checks.

Caption behavior follows [Microsoft's WindowChrome documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome).

At implementation time the owner authorized a local commit and hibernation, without
a version bump, push or release. That earlier power-state request is historical.
On 2026-11-17 the owner authorized GitHub source/docs synchronization; see
[ROADMAP.md](../ROADMAP.md) for the current scope. No release is included.
