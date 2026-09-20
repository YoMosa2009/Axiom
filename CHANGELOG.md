# Changelog

## [V1.9.3] - 2026-09-20

The Computer Agent: the Workplace model can now operate the machine directly.

### Added
- **Computer Agent.** Enable it in the Workplace sidebar and the model works like a terminal
  coding agent: it runs commands, reads and writes files, lists directories, and searches the
  disk, then answers from what it actually found. Replaces the narrower "connect a repo or a
  folder" framing of Codebase Edit Access, which stays available for patch review
- Seven tools, the set the established coding agents converged on: `run_command`, `read_file`,
  `write_file`, `edit_file`, `list_directory`, `find_files`, `search_text`, plus `finish`
- **Manual and Auto approval.** Manual stops before every command and every file change and
  shows an approval bar above the composer with Approve, Always allow this, and Deny. "Always
  allow this" remembers that command for the session, matched on its leading tokens so
  approving `git status` never quietly approves `git push --force`. Auto runs without stopping
- **Scope control.** The agent is confined to a folder you choose, or released to the entire
  computer. Both the whole-computer scope and Auto approval require an explicit confirmation
- Works on Local, Hybrid Local, and Cloud, and across model sizes. Capable models get JSON tool
  calls and up to 24 steps; sub-4B models get a flat `TOOL name` + `key: value` protocol they can
  actually produce, a smaller step budget, and trimmed tool output so results do not swamp their
  context
- **Running-step indicator.** A quiet one-line note above the composer names the step in flight
  ("Ran dotnet build", "Read App.xaml", "Waiting for approval") and disappears when the run ends

### Changed
- The Single Model toggle is now a full-width control that names the active mode with a role
  count, instead of a small chip that dimmed itself to 72% opacity and read as disabled

### Safety
- A short list of whole-machine operations is refused in both Manual and Auto: formatting a
  drive, creating a filesystem, repartitioning, deleting a drive root or the filesystem root,
  rewriting the boot configuration, wiping free space, shutting the machine down, and fork
  bombs. Writes into `Windows\System32` and `SysWOW64` are refused as well
- The block list is matched narrowly so ordinary work is untouched: `rm -rf ./build`,
  `del /q obj\temp.txt`, `git reset --hard`, and `npm run format` all run normally

## [V1.9.2] - 2026-09-18

### Added
- **Durable Project Knowledge Base.** Workplace can retain any number of files or an entire
  folder with the current project. Files are copied into project-owned storage, restored after
  restart, and can be managed or removed individually from the Project Knowledge Base screen.
- **Hybrid project retrieval.** Project Knowledge uses lexical, BM25, optional local semantic,
  reciprocal-rank fusion, and diversity ranking to supply cited passages within each model's
  context budget across local, hybrid, and cloud execution.

### Changed
- **OTA release versioning.** The running app version, GitHub release tag, package name, and
  in-app update notification now align on `v1.9.2` through the release pipeline's single
  `.csproj` version source.
- Removed the separate Council Session Memory / Study Session feature; Project Knowledge is now
  the durable project-scoped reference system.

## [V1.9.1] - 2026-09-14

Computer Use, app theming, attachment previews, and Skills that produce real deliverables.

### Added
- **Computer Use.** Type `@ComputerUse` with a goal in the Workplace composer and Axiom
  drives the desktop: screen capture, planning, mouse and keyboard input, and verification
  that each action actually landed. Runs in a dedicated session window with a pointer
  overlay and an action log, with safety checks on navigation and target selection.
  Requires a vision-capable model; Axiom probes the active model first and explains why a
  run cannot start instead of failing partway through
- **App themes.** Settings -> General switches the whole application between Axiom Dark
  and Gruvbox Dark. The change applies instantly with no restart and is remembered across
  sessions. Covers both chats, dialogs, the Effort picker, rendered chat bubbles, and the
  native Windows title bar
- **Attachment previews.** Uploaded files and images now show a thumbnail chip above the
  composer in both Normal Chat and Workplace, so you can see what you attached before
  sending
- **Positional attachment references.** "In the 3rd attached image" or "the first PDF" now
  resolves to the right attachment, on every model size and inference mode
- **Skills deliver rendered artifacts.** Slide Deck Studio, PDF Studio, and Data Analysis
  now produce a real deck, print-ready document, or charted report in the Project Canvas;
  Document Summarizer and Code Review answer in chat. Each Skill carries an explicit
  purpose and explicit limits
- Skills route to the Project Canvas automatically when an attached rendering Skill matches
  the request, with no `@ProjectCanvas` needed. Deliverable terms are narrower than
  activation terms, so a one-number question still gets a one-number answer
- Skills work below 4B parameters: a model too small to author correct HTML supplies a
  short structured outline instead and Axiom composes the artifact itself. The authoring
  contract is sized from the model's measured parameter count (sub-1B / 1-4B / 4B+), and
  cloud and unmeasured models get the full contract
- A Skills button in the Workplace composer. Attachments stay global across both chats,
  and the Builder receives the canvas contract at its own model tier
- Custom Skills can declare a rendered deliverable rather than only a chat answer
- Effort control for reasoning, generation, and tool budgets

### Fixed
- Hybrid Local endpoints that stream newline-delimited JSON (Ollama-compatible servers)
  returned "The model did not produce a response for this input." The stream parser only
  understood SSE `data:` framing and silently discarded every NDJSON chunk
- Reasoning models that emit `reasoning_content` or `thinking` instead of `reasoning` now
  stream correctly, and a `message` payload is accepted where `delta` is expected

### Changed
- Normal Chat: removed the sidebar-collapse control and the Recents header, replaced the
  right-edge Project Canvas rail with a toggle in the tab bar, and modernised the Web,
  Stop, and Send buttons
- Workplace: removed the stage bar, moved the context meters into the header, flattened the
  composer into a single card with more room for the prompt, and added a Workplace-only
  control to collapse the chat pane

## [V1.8.6] - 2026-08-14

Updater orphaned-instance fix -- the actual root cause.

### Fixed
- Found the real cause behind every update failure this cycle, including the ones the
  V1.8.4/V1.8.5 hotfixes narrowed but didn't fully close: a failed update automatically
  relaunches the installed copy (`TryRestartInstalledCopy`), and that relaunch always goes
  through the normal single-instance check. If another copy from an earlier failed
  attempt was still silently running -- its own "Axiom is already running" dialog never
  seen or dismissed -- the new relaunch hit that same dialog and became one more orphan
  itself, invisible and still holding the installed DLLs open. Each subsequent failed
  update compounded this; eleven separate Axiom processes had accumulated behind the
  scenes, several matching PIDs already logged as "did not exit" hours earlier
- The updater's post-update relaunch is now tagged as a silent restart; if it finds Axiom
  already running, it exits quietly instead of showing a dialog nobody may ever see or
  dismiss
- Before every update, the updater now sweeps for and terminates *any* process actually
  running the installed executable, not only the one process ID it was launched to wait
  for -- so a backlog of orphans from before this fix can't keep blocking future updates
  either

## [V1.8.5] - 2026-08-14

Updater file-swap reliability hotfix, part 3.

### Fixed
- Fixed the in-app updater still failing after the V1.8.4 hotfix, this time with:
  `Could not replace "...\Accessibility.dll.axiom-update-old" after retrying for 21s --
  another process kept it open the whole time.` A stock, unchanged-release-over-release
  WPF/.NET redistributable was held open continuously by an external process (most likely
  Windows Defender real-time scanning) for longer than any bounded retry should reasonably
  wait
- Root fix, not a longer timeout: the updater now compares each file's content to what's
  already installed before touching it at all, and skips the replace entirely when they
  already match byte-for-byte. Most files in a self-contained build (the .NET runtime,
  WPF, WindowsAppSDK, the CUDA backend, ...) are identical release over release -- only
  Axiom's own changed assemblies still go through the replace/retry path. This removes
  the entire class of "an external process has this exact unchanged file open" failures
  and makes ordinary updates noticeably faster besides
- update.log now records how many files were replaced vs. already up to date for each
  applied update

## [V1.8.4] - 2026-08-14

Updater file-swap reliability hotfix, part 2.

### Fixed
- Fixed the in-app updater still failing with "Access to the path is denied" on some
  machines after the V1.8.3 hotfix, because the previous retry budget (~3 seconds) was too
  short: update.log showed the file lock clearing in under a second on one machine and
  still being held 20+ seconds after the forced process kill on another
- Widened the file-swap retry to a 20-second wall-clock budget with capped exponential
  backoff, comfortably covering the slower observed case
- The error now names the exact file that stayed locked (previously the underlying
  .NET exception carried no path, making a real, non-transient lock impossible to
  diagnose from the log alone)

## [V1.8.3] - 2026-08-14

Updater file-swap reliability hotfix.

### Fixed
- Fixed the in-app updater intermittently failing with "Access to the path is denied" right
  after force-closing a slow-to-exit Axiom process, leaving the update cancelled and the
  previous version safely reinstalled but not applied
- Root cause: Windows can keep a just-terminated process's loaded EXE/DLL briefly locked (or
  a real-time antivirus scanner can grab the file the instant it's released) even after the
  updater already confirmed the process handle itself had exited, so the very next file
  rename could hit a transient sharing violation unrelated to real permissions
- The updater now retries the file swap with a short bounded backoff (up to ~3 seconds
  total) before treating a locked file as a genuine failure, so a brief post-kill lock no
  longer aborts the update; a truly stuck file still fails loudly and rolls back safely as
  before

## [V1.8.2] - 2026-08-14

Project Canvas thinking hotfix.

### Fixed
- Fixed `@ProjectCanvas` in Normal Chat appearing to hang on "Thinking" forever with no
  output, across Local, Hybrid Local, and Cloud modes
- Root cause: the `@ProjectCanvas` instruction reads as a demanding one-shot authoring
  task, which regularly pushed the automatic thinking gate (Local) or the reasoning
  request (Cloud/Hybrid via OpenRouter) on for these prompts. With thinking on, the chat
  bubble intentionally withholds every UI update until generation fully finishes, and a
  model can spend its entire completion budget drafting the artifact inside its hidden
  reasoning channel and never reach a visible answer -- reproducing as a static
  "Thinking" indicator that renders nothing once generation stops
- `@ProjectCanvas` requests now skip the reasoning pass in both pipelines so generation
  streams live and the model writes the artifact directly instead of drafting it in a
  hidden channel first
- Reinforced the `@ProjectCanvas` system instruction to tell the model to skip silent
  deliberation and answer directly, as a second layer of defense for models that reason
  internally regardless of this setting

## [V1.8.1] - 2026-08-14

Seamless updater shutdown hotfix.

### Fixed
- Fixed in-app updates stopping after staging because the original Axiom process could
  remain alive indefinitely during shutdown
- Added a bounded pre-update state save and shutdown watchdog so future updates close
  cleanly even when a background component does not terminate normally
- Made the staged helper verify the exact installed executable, process ID, and start
  time before terminating a stalled old process and continuing the requested update
- Removed completed download and staging folders after a successful update while
  preserving chats, settings, models, connectors, and other user data
- Kept package replacement transactional: old managed files and temporary backups are
  removed only after every v1.8.1 file has been staged successfully, then Axiom restarts
  from the updated installation automatically

## [V1.8.0] - 2026-08-14

Normal Chat Project Canvas release.

### Added
- Added `@ProjectCanvas` to Normal Chat's `@` menu for Local, Hybrid Local, and
  OpenRouter Cloud modes, with gold mention highlighting in the composer
- Added a responsive right-side Project Canvas with an adaptive collapsed handle,
  smooth open/close animation, automatic expansion after artifact completion,
  Preview and Source modes, source copy, and file export
- Reused Workplace artifact detection so Normal Chat can render self-contained HTML,
  SVG, interactive JavaScript, and formatted Markdown documents offline
- Made calculator and Python tools available when a Project Canvas request benefits
  from them, and added bounded Java compile/run support for explicit Java tasks

### Fixed
- Prevented rendered LaTeX, Markdown tables, and other rich messages from flashing
  back to raw source while scrolling by restoring recycled rows from a bounded
  rendered-image cache
- Cleared the Normal Chat canvas when starting or switching chats so artifacts do not
  leak between conversations

## [V1.7.2] - 2026-08-13

Updater reliability hotfix.

### Fixed
- Closed the downloaded update file before checksum verification, preventing the
  updater from locking its own completed `.partial` ZIP and reporting a failed download
- Added regression coverage that opens the completed download exclusively for verification
- Fixed clean-checkout release automation by restoring test dependencies before packaging

### Updating
- V1.7.0 and V1.7.1 users must install V1.7.2 manually once because the affected
  updater cannot install its own repair; in-app updates work normally from V1.7.2 onward

## [V1.7.1] - 2026-08-13

Cloud reliability patch.

### Added
- Added a one-command GitHub release workflow that tests, packages, generates
  changelog-based release notes, creates the version tag/release, and uploads the ZIP
- Added configurable updater storage through `AXIOM_UPDATE_DIR`; this development
  computer uses `E:\Axiom-Updates` for release packages, downloads, and staging
- Added a concise release-note summary to the in-app update notification
- Removed unused Linux native binaries from the Windows release package and replaced
  the unreliable PowerShell archive step with direct validated ZIP creation

### Fixed
- Removed the blocking Workplace cloud activation probe that could remain on
  `Validating` after a provider accepted the request but never returned a body
- Added bounded first-stream and first-content deadlines so stalled OpenRouter
  providers automatically move through Axiom's fallback chain instead of leaving
  Normal Chat on `Thinking`
- Reduced idle and total stream ceilings while retaining enough time for long,
  actively streaming coding responses
- Fixed streamed answer text being duplicated into the hidden reasoning channel
- Added regression coverage for plain and structured OpenRouter content separation

## [V1.7.0] - 2026-08-13

Agent modes, reusable capabilities, desktop integration, and in-app delivery release.

### Workplace
- Added Single Model mode: one agent plans, uses tools, executes, and verifies without
  Architect, Builder, or Critic role handoffs
- Added a Workplace header toggle for switching between Council and Single Model modes
- Preserved tools, Project Canvas, codebase access, attachments, context controls, session
  memory, persistence, and cloud/local execution across both modes

### Skills and Plugins
- Replaced the Normal Chat Templates and System buttons with Skills and Plugins panels
- Added five global built-in Skills: PDF Studio, Slide Deck Studio, Document Summarizer,
  Data Analysis, and Code Review
- Added user-created instruction Skills with locally persisted attachment state
- Added Web Research, Data Lab, File Intelligence, Connected Apps, and Creator Studio Plugins
- Attached capabilities apply across Local, Cloud, Hybrid Local, Council, and Single Model modes

### Models
- Renamed Edios 1 to Edios 1.5 and moved it to Google Gemma 4 31B (free)
- Renamed Hepha 1 to Hepha 2.5 Coder and moved it to NVIDIA Nemotron 3 Ultra (free)
- Added model-specific prompting and tool behavior for the new cloud profiles

### Desktop and updates
- Added explicit background-operation and system-tray controls in Settings
- Hidden tray mode suspends UI activity and releases heavy local model caches after active work
- Replaced application, taskbar, and Normal Chat branding with the Axiom logo
- Added stable GitHub Release checks, in-app update notifications, verified ZIP downloads,
  safe staged replacement, automatic restart, and protected local user data
- Added clean release packaging scripts, update manifests, release documentation, and tests

## [V1.6] - 2026-8-11

Workflow, focus, and polish release: the council keeps one model for the whole task, the app
tells you when your API key runs out, and the workplace chat got a visual overhaul — plus a
second improvement batch: first-run model downloader, chat search/pin/rename, native markdown
council cards, Builder vision, diff-aware Critic reviews, exact cloud token accounting,
hardened offline canvas, completion toasts, a Settings log viewer, and a unit test suite.

### Cloud & council behavior
- The Workplace Council no longer falls back to a different cloud model mid-task — a silent
  model swap left the replacement model unable to continue what the first model started. The
  council now stays on its selected model, recovering with a bounded same-model retry instead
- New API-key exhaustion detection: when the OpenRouter key is out of credits (402) or past its
  free daily quota, both Normal Chat and the Workplace show a clear notification instead of a
  generic provider error
- Every AI model in the app (cloud and local, chat and council) now runs on a permanent hidden
  truth-first foundation layer beneath all feature prompts

### Settings
- The API usage display under the OpenRouter key is now a pill-shaped meter showing used,
  limit, and remaining requests, with a low-quota/exhausted warning line

### Workplace chat UI/UX
- Header status items are now clean chips instead of one dot-separated line
- Chat cards were redesigned: real separation between turns, breathing room, a slim rounded
  role-accent rail, warmer card tints that match the app theme, and better line spacing
- The Agentic Pause banner uses the app's gold accent instead of error-red

### Performance
- Live streaming previews (council and cloud chat) are throttled to UI-rate updates instead of
  re-rendering the full text on every token — long deliverables no longer saturate the UI thread
- Workplace card brushes are shared and frozen; formatted card text is cached per change
- Session memory (Hippocampus) caches keyword sets; queries, consolidation, and dedup no longer
  re-run regex extraction per entry
- PDF text extraction line grouping is linear instead of quadratic on dense pages

### Features
- Web search: sub-queries now run concurrently, tracking parameters are stripped so duplicate
  articles dedup correctly, and stable docs/reference lookups are cached longer
- Study Session: one failed chunk no longer aborts the whole run — it is skipped and logged
- Council session-memory tool results now include their source/tag labels so models can weigh
  studied references against prior role outputs

---

## [V1.5] — 2026-07-08

Reliability and intelligence release: cloud chat can no longer hang, local models of every size
use tools safely, and the Codebase Access pipeline verifies that patches actually implement the
request.

### Cloud reliability
- Fixed the endless-loading hang in cloud Normal Chat: streamed responses now have idle/total
  deadlines, mid-stream provider errors are detected, and all failure shapes automatically fall
  back to the next cloud model — partial answers are kept instead of discarded
- All response reads are time-bounded; stalled providers can no longer freeze a turn

### Local model intelligence & tools
- Local models are now profiled into size classes (<1B / 1–4B / 4–10B / 10B+) and the pipeline
  scales tool routing, context budgets, and prompts to what each class can actually handle
- New deterministic tool intent router: calculations, unit conversions, current-info lookups,
  file reads, codebase searches, and session-memory recalls are detected from the request itself
  — hallucination-free, for every model size
- Tool calls are semantically validated (invented numbers, unknown file paths, and off-topic
  queries are rejected with a targeted correction), and tool results are digested into plain
  facts for small models so they use them instead of echoing them
- Mid-generation [PAUSE:] tool calls tolerate the syntax drift small models produce, and pause
  budgets scale with model size
- New per-model tool reliability ledger: models that route tools well earn extra calls; models
  that misroute are stepped down automatically
- Sub-1B models no longer hallucinate tool output (the "PYTHON_MATH / execution output" failure)

### Council / Codebase Access
- Patches are now checked for requirement relevance: a patch that modifies unrelated code
  instead of implementing the request is rejected and retried with a targeted correction
- Applying a patch no longer leaves ".bak" files in the connected workspace, and stale ones are
  cleaned up
- The post-apply Git report now separates pre-existing working-tree changes from changes made by
  the patch

### Production hardening
- Single-instance guard: a second Axiom instance can no longer silently corrupt chats/settings
- Crash dialogs are now user-friendly and point to the diagnostic log; full stack traces go to
  the log file, and background task failures are always recorded
- Diagnostic logs are size-capped with rotation instead of growing forever
- PDF validation no longer misreads short files; removed the deprecated System.Data.SqlClient
  dependency and dead code

---

## [V1.2] — 2026-06-05

First official release of Axiom — a major overhaul.

- Major improvements to the council backend/pipeline and role fixes
- Web search fixes and improvements
- New cloud models
- GPU usage improvement (Nvidia)
- Context improvements
- New feature: Artifact Rendering
- Qwen3-Coder-480B-A35B-Instruct added as the council AI model in Workplace Council Mode

---

## [v0.01] — 2026-05-06 (Pre-release)

Initial pre-release of Axiom. Released to gather reviews, suggestions, and real-world usage feedback.

> This is a pre-release. Bugs and unexpected behavior may occur.
