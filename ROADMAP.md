# Axiom roadmap and work tracker

What has shipped, what is waiting on the owner, and what comes next. Update this file in the
same commit as the work it describes, so nothing agreed in a conversation gets lost.
`CHANGELOG.md` is the user-facing history; this file is the working list.

## Waiting on the owner

| Item | What to do | Why it matters |
| --- | --- | --- |
| **winget listing** | In PowerShell: `wingetcreate submit --token (gh auth token) F:\Axiom-Releases\winget-1.11.0` (manifests validated 2026-10-10). Microsoft reviews it within a few days; then `winget install YoMosa2009.Axiom` works. Afterwards each release: `wingetcreate update YoMosa2009.Axiom --version X.Y.Z --urls <Setup.exe URL> --submit --token (gh auth token)`. | People find and install Axiom from the Windows package manager. |
| **Code signing** (deferred, owner decision 2026-10-09) | When the user base grows: Azure Trusted Signing (about $10/month, identity validation required), then sign `Malx_AI.exe` and the release ZIP's executables in `release.yml`. | Removes the "Windows protected your PC" SmartScreen warning on first launch. Until then README's Getting started explains **More info → Run anyway**. |
| **CI release secrets** | Confirmed missing on 2026-10-09 (the V1.10.0 CI run stopped before publishing). In GitHub: repo **Settings → Secrets and variables → Actions → New repository secret**, add `AXIOM_BUILTIN_GOOGLE_CLIENT_ID`, `AXIOM_BUILTIN_GOOGLE_CLIENT_SECRET`, `AXIOM_BUILTIN_GITHUB_CLIENT_ID`, `AXIOM_BUILTIN_TODOIST_CLIENT_ID`, `AXIOM_BUILTIN_TODOIST_CLIENT_SECRET` with the same values as the local OAuth files. | Until then every release has to be published from this PC with `scripts/Publish-GitHubRelease.ps1`. |

## Committed, not yet released (2026-11-16)

### Local reliability, UI and context work (2026-11-16, not released)

Additional UI/context fixes after the 22:23–22:29 owner test:
- OpenRouter usage now reads the explicit daily free-model request counter, reports the
  remaining percentage, and fills the pill with the selected theme accent. Refreshes are
  bounded, expire after 30 seconds/UTC rollover, and ignore responses for replaced keys.
- GPT 6.2 Sol user-flow testing: fixed sticky Settings navigation/scroll reset, first-click
  compact Workplace sidebar reopening, and rapid main-tab navigation losing the final click.
- Fixed custom-profile isolation: Debug migrations and shared OAuth credentials no longer
  import into or write outside explicit custom profiles. Default profiles retain migration.
- Atomic saves now preserve unrelated `.tmp` siblings, serialize commit/backup updates,
  and leave the primary/backup unchanged when canceled before commit.
- Follow-up layout correction (2026-11-17): restored the native Windows caption and its
  minimize/maximize/restore/close buttons. Navigation and Workplace controls share a client
  toolbar with automatic height; actions move below the title when space is tight.
- Normal chat and Workplace message lanes follow their composer widths as sidebars/panels
  open and close. Normal chat gutters respond to the remaining chat column width.
- Simplified the Workplace composer: review policy, web search and skills live in the
  attachments/tools menu. The idle Stop button is hidden; effort uses its compact selector.
  Normal chat model choices and run controls use separate aligned rows when space is tight.
- Enlarged Workplace Context/Environment typography and row spacing, with wrapping budget
  labels and responsive output filenames. Removed the duplicate floating sidebar arrow;
  the navigation toggle changes direction and accessibility label for show/hide states.
- Messages and composer share a centered 960-pixel conversation region when sidebars collapse.
- Computer Use remains the active surface across follow-ups, carries its prior goal/result/app,
  and restores that per-chat context from snapshots. A visible composer control exits desktop
  mode. It captures the prior app before acting; a missing/minimized app is not substituted
  with whichever unrelated app is in front, and desktop input is refused until a target exists.
- Added durable per-chat full conversation/tool evidence and read-only `recall_context` paging.
  Hooks cover native Chat/Council calls, MCP, Python, local council pause tools, Agent calls
  (including refused actions), full command/background output, and Computer Use plans/actions/
  results/screenshots. Local Agent messages no longer have the arbitrary 250-character cut.
  Models still have finite prompt windows; full retained evidence can be recalled instead of
  assuming the shortened prompt contains everything. Earlier versions' discarded raw tool
  output cannot be reconstructed retroactively.
- Validation: 900 tests passed; Debug app and simulation harness builds passed; actual-window
  private-desktop render confirmed the unified caption and centered collapsed-sidebar layout.
  See `docs/WORKPLACE_LAYOUT_CONTEXT_20261116.md` for evidence and acceptance limits.

Follow-up after owner testing (20:24–20:32): the latest visualizer task used 13 steps,
with fast file writes and bounded screenshots; playback was explicitly left unverified.
The model still used shell focus/window-control workarounds between captures. These are
now blocked from the first attempt in both the session and tool executor, including background
commands and native ShowWindow/SetForegroundWindow variants. This is an execution guard,
not a keyword router for interpreting user messages.

Generated pages can now use `take_screenshot(url=...)` to render in an isolated headless
browser without opening a visible browser or changing desktop focus. Per the owner's reference,
captures appear as compact "Viewed an image" dropdowns with 80-pixel thumbnails only in the
live thinking/activity card. Completion, cancellation or interruption removes the previews;
they are excluded from final responses and saved chat snapshots.
Default saved captures live in the app profile's `WorkplaceCaptures` folder. The background
browser uses its own temporary profile and a 25-second timeout. An initial render does not
establish that interactions or animation playback work.

Verification and latest log analysis: `docs/WORKPLACE_CAPTURE_FOLLOWUP_20261116.md`.

- Reviewed the latest saved Workplace conversation (18:48–19:03): a transformer animation
  remained blank after 20-step and 44-step follow-ups; the model asserted a stale-tab cause
  without establishing it and spent many turns reading small slices of the same file.
- Live follow-ups now give the model the current message and factual prior-run context to
  interpret. Phrase matching no longer selects question/change/resume routing, tool access,
  or replay of old steps. Status questions, corrections and task switches are interpreted by
  the model. Legacy classifier helpers are retained for compatibility, outside the live path.
- `read_file` now fits the observation budget and states its actual continuation line;
  `search_text` accepts a single file as well as a directory. Repeated inspection of one
  unchanged file is limited in Full tier, with fresh inspection allowed after an edit.
  Micro/Compact reads fit their smaller transcript budgets; their flat protocol does not
  receive the search-based inspection guard because it does not expose `search_text`.
- Browser diagnosis instructions distinguish syntax/launch success from working playback and
  forbid presenting an unproved stale-tab or test-stub explanation as a confirmed cause.
- Agent captures default to an external work window without hiding Axiom. Unsupported or
  minimized targets return an explicit failure; there is no fallback to pixels from a covering
  window. Capture waits are bounded to five seconds with at most one native worker in flight.
- Computer Use no longer minimizes/restores/activates Axiom at session boundaries. When Axiom
  is in front, its capture uses the external target's image with screen coordinates preserved.
- Cloud/Hybrid agent decisions have a three-minute deadline across retries. Separate model
  and tool timing logs identify which part is slow; completed slow steps show their duration.
- Passive `tools/axsim capturecheck <workdir> <window>` probe captured an existing Edge window
  in 183 ms with visibility/minimized state and foreground focus unchanged (seven windows
  checked). Axiom was not running: full in-app and live-model acceptance remain unverified.
- Regression/build results and limits: `docs/WORKPLACE_RELIABILITY_20261116.md`.

Agent mode and Computer Use reliability work, on `main` without a version bump, so no OTA
update has gone out. Ship it as the next MINOR version when the owner says so.

- **Computer Use drawing:** drags are injected mouse moves in small steps with holds, and a drag
  can carry several strokes and shapes. The controller measures each drag on screen
  (`[DRAG VERIFIED]` / `[DRAG NO EFFECT]`). Proven on real Paint on 2026-11-16 with
  `tools/axsim dragprobe` (3/3 strokes drawn, Rectangle tool click switched its state).
- **Computer Use judging:** one step verdict per action (controller evidence, then observer, then
  screen change); tool buttons report `state=on/off/selected`; the canvas is passed as a work
  area, clipped to its window; target clicks are labelled by target id, not "(0, 0)".
- **Agent follow-ups:** questions are answered without working (3 read-only steps at most),
  change requests stay narrow, a verification budget after the first file change, one closing
  summary at the step ceiling; `take_screenshot` can capture a named window without focusing it.
- **Small local models (Micro/Compact):** both tiers get `write_file` and a `finish` example; the
  rules only name tools the tier has; the transcript shows full commands; the run record lists
  commands one per line.
- **Simulation harness** `tools/axsim` (see `tools/simcli.py`): a subagent plays the model against
  the app's real Agent and Computer Use code, including a simulated Edge. Reports:
  `tools/axsim/last-*-sim-report.md`.

Not verified yet: real sub-1B/1-4B GGUF models with the new prompts, a real (not simulated)
browser run, and `CaptureForAgent` on a minimised or GPU-heavy window.

## Shipped

### V1.11.0 (2026-10-10)
- Windows installer (`installer/Axiom.iss`, `scripts/Build-Installer.ps1`), built from the release ZIP's contents.
- Send feedback sends directly through Web3Forms (key `bc1fcf77-…`, tested 2026-10-10), with the email fallback kept.
- "Post as a GitHub issue" link, issue forms, PR template, CONTRIBUTING.md, SECURITY.md, CODEOWNERS.
- License changed to PolyForm Noncommercial 1.0.0.
- Repository protection: `main` ruleset (pull request + code-owner approval + passing CI for everyone but the
  owner), outside contributors' workflow runs need approval, private vulnerability reporting on.
- Local releases go to `F:\Axiom-Releases`, and older local packages are deleted after each upload.

### V1.10.0 (2026-10-09)
- First-run guide with hardware-aware model recommendation (`FirstRunGuideWindow`, `LocalModelFitAdvisor`).
- Send feedback in Settings › General (`FeedbackDialog`, `FeedbackReport`, `FeedbackDelivery`).
- Agent Access undo (`AgentRunJournal`, `WorkplaceView.AgentUndo.cs`).
- Reply speed and size in every mode (`GenerationStats`, `GenerationTelemetry`, `GenerationReadout`).
- Redesigned Chat and Workplace sidebar (`MainWindow.Sidebar.cs`, `SidebarHistoryGroups`).
- Fixed: compacted agent history claimed refused/blocked steps ran.

Built with the planner/implementer split: Claude did the UI, wiring and anything risky; local
Qwen3.6 did five contained pieces in a separate worktree (model-fit rating, feedback report
builder, speed stats, the compaction fix, the undo tests), each reviewed before merging.

## Next up (candidates, in rough priority order)

1. **Workplace polish.** One look for run cards and status lines across Council, Single Model
   and Agent Access; quieter notifications.
2. **Split `WorkplaceView.xaml.cs`** (about 20,000 lines) into partials by feature. No user-visible
   change, but every later Workplace fix gets faster and safer.
3. **Exact token counts for Hybrid Local.** Ask the server for usage in the stream
   (`stream_options: {"include_usage": true}`, the OpenAI-style switch; check what llama.cpp,
   Ollama and the gateway actually return) so the speed readout stops estimating.
4. **Feedback attachments.** A screenshot or the full log, once a delivery service that accepts
   files is chosen (Web3Forms' free tier does not).
5. **Undo for earlier runs**, not just the latest, when no later run touched the same files.

## Known issues

- Agent Access undo cannot reverse changes made by shell commands (installs, files a command
  deleted); the undo bar says so.
- The Gemma 4 CLI runner and a few internal helper prompts don't report to the speed readout.
