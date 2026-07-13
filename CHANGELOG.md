# Changelog

## [Unreleased]

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
