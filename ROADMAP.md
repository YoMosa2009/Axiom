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
