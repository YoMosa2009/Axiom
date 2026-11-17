# Workplace reliability investigation — 2026-11-16

## Evidence

Read the real release/development backend logs and the newest development Workplace chat
embedded in `ChatHistory/chat_advanced_state.json` (not just `workplace_session.json`, whose
standalone snapshot was empty). The most recent saved conversation asked for a transformer
inference animation at 18:48, continued at 18:56, and reported broken playback at 19:00.
Backend run records show 4, 20 and 44 steps respectively. The initial run hit exhausted
OpenRouter credits. Later summaries described a blank canvas, unproved stale-tab explanations,
many repeated reads, and an inconclusive Node test with a fake DOM. These summaries are model
reports, not independent proof of what caused the animation defect.

Confirmed implementation problems:

- Reads assembled up to 60,000 characters, then the next observation truncated to 6,000.
- Text search rejected a file path as a nonexistent directory.
- Phrase classification chose follow-up routing, policy and history replay.
- Unnamed agent screenshots hid/re-showed Axiom; native window capture could restore a
  minimized target or fall back to pixels belonging to an overlapping application.
- Computer Use explicitly minimized Axiom at the beginning and restored/activated it at the end.
- Agent decisions could inherit long transport deadlines and retries without an overall limit.

## Implemented behavior

Follow-up interpretation belongs to the model. Every follow-up receives the current request,
the task record, relevant file paths, previous outcome and conversation. The live path no
longer invokes the phrase classifier. Execution guards constrain repeated calls independently
of the words used in the request. Existing classifier helpers remain compatibility code.

Reads stop within their observation budget and give the real continuation line. Search accepts
files or directories, retaining project confinement. In Full tier, six successful reads of one unchanged file
trigger targeted-search guidance; changing that file resets its read count. Path aliases share
the same count. Micro/Compact reads fit their smaller transcript budgets, and are not blocked
with advice to use the unavailable `search_text` tool. Browser instructions require relevant runtime/visual evidence, and summaries
must distinguish verified results from uncertainty.

Agent screenshots use a named window, or the top external application by default. Axiom remains
visible. Unsupported/minimized targets fail explicitly; pixels from another window are never
substituted for the requested window. Native capture is bounded to five seconds; a timed-out
worker retains ownership of its resources and keeps the single-worker gate until it returns.
Windows cannot cancel `PrintWindow` itself. Computer Use also keeps the host visible and can
capture a covered target while preserving its screen-coordinate mapping. Actual desktop input
still activates its target application as required to perform the requested action.

Each Cloud/Hybrid model decision has a three-minute deadline covering retries. A timeout stops
the run with the completed steps preserved and the pending call unexecuted. New
`ComputerAgent.ModelTurn` and `ComputerAgent.Tool` events record elapsed milliseconds separately.
No API keys or tool arguments are added to these diagnostics.

Microsoft documents that `PrintWindow` is synchronous and may block:
https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow

## Validation and limits

- Baseline Debug build: zero errors, existing warnings. Baseline sandboxed tests could not
  connect to their test host (socket error 10013); increasing the timeout did not fix access.
- Final regression suite outside the sandbox: 883 passed, zero failed or skipped (33 seconds).
  This includes follow-up context, inspection reset/path aliases, file search confinement,
  real continuation-line reporting, small-model observation budgets and capture error reporting.
- Final Debug app build: zero errors (352 existing warnings). Final capture-harness build:
  zero errors. `git diff --check` passed; the AST-only Graphify update completed.
- Capture harness build: zero errors. Passive capture of an existing Edge window: 1280 × 817,
  183 ms, seven windows checked, visibility/minimized state and foreground focus unchanged.
  It sent no input and launched no application. Axiom itself was not running during the probe.
- The native capture method rejects a uniformly colored result conservatively; a genuinely
  solid-color app can therefore be reported as unsupported. GPU/protected surfaces may refuse
  capture. Minimized targets are not restored for inspection.
- No paid/free model requests were spent, no Hybrid Local workload was interrupted, and no
  live-model or full in-app Computer Use acceptance is claimed. The user's animation project
  was not modified. These changes are local and unreleased.
