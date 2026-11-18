# Axiom source handoff — 2026-11-17

Repository: [YoMosa2009/Axiom](https://github.com/YoMosa2009/Axiom).
Read [CLAUDE.md](CLAUDE.md), [ROADMAP.md](ROADMAP.md) and
[AXIOM_IMPLEMENTATION_TRACKER.md](AXIOM_IMPLEMENTATION_TRACKER.md) for architecture,
current work, verification and limits. AGENTS.md, RELEASING.md and VERSIONING.md
cover repository navigation and shipping. This document assigns no new task.

## Current state and authorization

- Windows WPF app on .NET 10, Windows 10.0.19041+. Historical project/executable
  names are `Malx_AI` / `Malx_AI.exe`; tests are in `Malx_AI.Tests`.
- Views: Chat, Workplace and Persona. Neuron navigation and user-facing Architect
  controls were removed; legacy Council role code remains.
- Latest published release verified on GitHub: **V1.11.0**, 2026-10-10.
  The project version and release badge remain unchanged.
- On 2026-11-17 the owner authorized synchronizing recent source and documentation
  commits to GitHub. **No release, version bump, tag or asset upload is authorized.**
  A source push is separate from an OTA release.
- Relevant source commits, in order: `c370e03`, `a8d02cb`, `5ede4a6`, `cf58509`,
  `83b49cc`, `7b6ff94`. Their scopes and detailed reports are linked in ROADMAP.md.

## Recent implementation

- Computer Use preserves its prior goal, result and application across follow-ups
  and saved chats. Screenshots use passive window capture or isolated page rendering;
  Agent capture commands cannot minimize, hide or focus desktop windows as a workaround.
- Full conversation/tool evidence is retained with recall paging. Finite prompt
  windows still require compaction; retained evidence is not equivalent to unlimited
  in-prompt context, and older discarded output cannot be reconstructed.
- Shell completion observes process exit independently of stream EOF, fixing false
  timeouts when launched GUI children inherit redirected output handles. Raw command
  diagnostics remain archived; replaying the same transport message object is deduplicated.
- Native caption buttons are restored. Chat and composer widths adapt to panels;
  Context elements are larger, with one sidebar toggle and a simpler composer.
- Cloud usage uses the explicit daily free-model request quota, handles unknown/error
  states and stale/key-replaced responses, and fills the percentage pill with the theme.
- Test-driven navigation, isolated-profile import/OAuth and atomic-save defects were fixed.
- Workplace Send spins while working. Completed activity collapses into a themed
  dropdown, retaining every recorded step. Finalization cannot be overwritten by
  queued live step updates.
- Background completion toasts request the Windows default notification sound.
  Existing notification settings and OS sound policy remain authoritative.
- Canvas has a useful empty state, preview/source/save actions, responsive controls,
  distinct diff views and theme-aware syntax contrast.
- Completed replies with recorded edits end with a reference-style change panel:
  counts, three-file preview, expansion, captured review and latest eligible Agent Undo.
  Snapshots retain the captured diffs; restored chats do not regain live Undo authority.
  This covers file-tool/applied patch edits and Canvas, not arbitrary shell/desktop edits.

## Latest recorded validation

- Full portable suite: **954 passed, 0 failed/skipped**, 58 seconds.
- WPF Debug build: **0 errors**, 352 existing warnings.
- Completion-panel fixture: **19 checks passed**, including real owned-file Undo,
  patch/Canvas integration, saved history, diff review and narrow/theme layouts.
- Previous run-experience pass: **18 progress/activity checks** and **24 Canvas checks**.
- Earlier cloud/user-flow pass has its own dated counts and limits in
  [the test report](docs/CLOUD_USAGE_STRESS_TEST_20261117.md).
- These are recorded implementation results, not checks rerun by this docs-only update.

## Acceptance limits and testing rules

- The owner confirmed the latest generated photo editor eventually worked. Targeted
  harness fixes do not establish general model reliability or certify new live tasks.
- Native toast delivery/audio has not been exercised on the owner's desktop.
  WebView content was checked through native DOM because WPF bitmaps exclude HWND pixels.
- The native attachment picker lacks full end-to-end acceptance; real OAuth sign-in
  and live model generation were not part of the isolated UI acceptance passes.
- Prefer fresh `AXIOM_DATA_DIR` profiles and private-desktop UI fixtures. Verify no
  user credentials or legacy data were imported. Do not assume a custom profile
  bypasses the app's normal single-instance mutex.
- Use UI Automation for Axiom controls. Do not send mouse/keyboard input, focus
  windows or close the owner's app for routine isolated QA. Do not interrupt an
  occupied Hybrid Local endpoint or spend model quota without task authorization.
- NuGet override: `$env:NUGET_PACKAGES='C:\Users\user\.nuget\packages'` in PowerShell.
  Build `Malx_AI/Malx_AI.csproj -c Debug`; test `Malx_AI.Tests/Malx_AI.Tests.csproj`.
  New pure-logic files must be linked into the test project; process tests share
  `ProcessLifecycleCollection`.
- Inspect Graphify first for code navigation; verify exact source facts before editing.
  Use Serena semantic navigation when available, and refresh Graphify after code changes.
- Read the user's latest request before acting. No additional work or release is
  assigned by this handoff; historical suggestions are not current authorization.
