# CLAUDE.md: working on Axiom

Axiom is a Windows desktop AI app: WPF on .NET 10, Windows 10.0.19041+. The project folder is
`Malx_AI/` for historical reasons, and the executable is `Malx_AI.exe`. The GitHub repo is
`YoMosa2009/Axiom`, and installed copies update themselves from its GitHub Releases ("OTA").
Read this file before changing anything. Most sections exist because something broke in a way
that was not obvious.

`AGENTS.md` (graphify usage, and the cloud-session "ship it" workflow), `RELEASING.md` and
`VERSIONING.md` are still authoritative for their topics. This file does not repeat them.

---

## 1. What Axiom is

| View | What it does |
| --- | --- |
| **Chat** (Normal chat) | Single-model chat. Supports Skills/Plugins and Project Canvas artifacts (HTML, PDF and similar render in a WebView2 side pane). Supports MCP connectors. |
| **Workplace** | A "Council" (Architect → Builder → Critic) or **Single Model** agent. Features: Codebase Access (patch envelopes into a connected folder), **Agent Access** (a terminal-style coding agent with real tools), **@ComputerUse** (vision-driven desktop control). |
| **Persona / Neuron** | Persona memory and related views. |

Three inference backends, chosen per view:
- **Local**: GGUF models through LLamaSharp (CUDA when an NVIDIA GPU is detected, otherwise CPU AVX/AVX2).
- **Cloud**: OpenRouter, with free-tier model aliases defined in `OpenRouterChatService.cs`.
- **Hybrid Local**: a user-configured OpenAI-compatible "custom endpoint". The owner runs their own (see §7).

## 2. Build, test, run

```bash
# NuGet's global folder is configured on E:, which is often unmounted or full. Always override:
NUGET_PACKAGES="C:\Users\user\.nuget\packages" dotnet build Malx_AI/Malx_AI.csproj -c Debug
NUGET_PACKAGES="C:\Users\user\.nuget\packages" dotnet test Malx_AI.Tests/Malx_AI.Tests.csproj
```

- **Tests:** about 660 xUnit tests, about 20 s on this machine (about 2 min inside OpenCode's shell). Keep them green.
- **How the tests compile:** `Malx_AI.Tests` targets plain `net10.0` and compiles *linked production files*
  (`<Compile Include="..\Malx_AI\...">`), not a project reference. A new pure-logic file must be added
  to the `.csproj` link list. Code that touches WPF, WebView2 or `AppDataPaths` cannot be linked, so keep
  testable logic in dependency-free files, e.g. `Agent/AgentRunSupport.cs`, `ComputerUse/*Verification.cs`.
- **Process-killing tests:** tests that start or kill processes (UpdateApplyService kills `powershell.exe`)
  share `[Collection("ProcessLifecycleCollection")]` so they don't run in parallel with tests that launch PowerShell.
- **Data profiles:** a Debug build uses the profile `%LOCALAPPDATA%\Axiom-Dev`; Release uses `%LOCALAPPDATA%\Axiom`.
  Set `AXIOM_DATA_DIR=%LOCALAPPDATA%\Axiom` to run a dev build against the owner's real settings and keys.
- **Logs:** `<profile>\logs\backend-events.log` and `backend-errors.log`. Read them first when diagnosing a user report.
  They have timestamps, `[ComputerAgent] tier:… steps:N/M cancelled:…` lines, stream failures and custom-endpoint metadata.
- **Single instance:** a mutex (`Local\Axiom_SingleInstance`) allows only one copy to run. Close the user's copy before launching a test build.
- **Graphify:** after code changes, run `graphify update .` (see AGENTS.md).

## 3. Releasing (OTA)

- **Cloud CI path (default):** merging to `main` with a new `<Version>` runs `.github/workflows/release.yml`,
  which tests, packages, tags and publishes. **Merges that don't change `<Version>` publish nothing.**
- **Version locations:** `Malx_AI/Malx_AI.csproj` `<Version>`, the `AppVersionLabel` in `MainWindow.xaml`,
  the `README.md` badge, and a dated `CHANGELOG.md` entry, which becomes the release notes. Follow `VERSIONING.md`.
- **Local path:** `scripts/Publish-GitHubRelease.ps1 -OutputRoot "C:\Users\user\Axiom-Updates"`. Its default output
  is on E:, which may be full or unmounted. It builds a roughly 415 MB clean ZIP and uploads it, which takes several minutes.
- **Encoding:** the script reads CHANGELOG.md as UTF-8. Before that fix, PowerShell 5.1 turned ✓ into `âœ“`.
  After publishing, check the release notes for mojibake (`gh release view vX.Y.Z --json body`).
- **Shipping rule:** the owner expects each finished fix to be committed, pushed and released so users get it.
  Follow AGENTS.md's ship workflow. Never delete or overwrite a published release.
- **Concurrent pushes:** `main` sometimes moves in parallel (cloud sessions, PRs). `git fetch` and rebase before pushing.

## 4. Repo map (where things live)

| Area | Files |
| --- | --- |
| Main window and Normal chat | `MainWindow.xaml(.cs)` plus partials: `.Cloud.cs` (cloud tool loop), `.Inference.cs`, `.ProjectCanvas.cs`, `.ResponsiveLayout.cs`, `.Mcp.cs`, `.ToolAgents.cs`, `.Theme.cs` |
| Workplace | `WorkplaceView.xaml.cs` (~19k lines: council pipeline, cloud role tool loop, patch capture), `.Agent.cs` (Agent Access UI and run), `.ComputerUse.cs`, `.SingleModel.cs` |
| Cloud and Hybrid transport | `OpenRouterChatService.cs` (model profiles, streaming, tool calls, fallbacks, custom endpoint), `OllamaStreamChunkConverter.cs`, `CustomEndpointMetadataParser.cs` |
| Agent Access | `Agent/`: `AgentSession` (loop), `AgentToolExecutor` (tools), `CloudAgentModel` / `TextProtocolAgentModel` (`AgentModel.cs`), `AgentPromptBuilder`, `AgentContextManager` (continuation and compaction), `AgentWorkVerifier`, `AgentRunSupport` (mid-run inbox, file backups), `AgentPermissionPolicy`, `AgentToolSchemas` |
| Computer Use | `ComputerUse/`: session controller, task contract, application/browser verification, planning, safety |
| Project Canvas / artifacts | `ArtifactRenderService.cs` (detection, fit-to-width normalize script, conversational reply builder), `SkillCanvasDirective.cs`, `AxiomCapabilityRegistry.cs` |
| Codebase patches | `WorkspaceAccessService.cs` (parse/apply `[[AXIOM_CODEBASE_PATCH]]` envelopes) |
| Local models | `LocalModelCapabilityProfile.cs` (size classes, reasoning detection), `ReasoningParser.cs`, `LocalToolIntentRouter.cs`, `ToolReliabilityLedger.cs`, `AgenticPauseEngine.cs`, `NativeBackendInit.cs` |
| Python sandbox | `PythonExecutionService.cs` (pythonnet) |
| Updater | `UpdateCheckService.cs`, `UpdateApplyService.cs`, `UpdateStoragePaths.cs`, `UpdateReleaseParser.cs` |
| Persistence | `DatabaseService.cs` (SQLite; DPAPI-encrypted keys), `JsonChatPersistence.cs`, `ChatWorkspaceStatePersistence.cs`, `AtomicFileWriter.cs` (`.bak` sidecars) |

## 5. Owner rules and preferences

- **Workplace Cloud mode uses exactly ONE model, with no fallbacks or backups.** It is currently
  `dots-studio/dots-3-note-preview:free` (text and image input, 512K context) behind the alias id
  `workplace-gpt-oss-20b`, with `AlternativeApiModelIds: []` and `GetFallbackModelId → []`. Normal chat is separate and may keep its fallback chain.
- The owner's free OpenRouter key allows **50 requests/day**. Use it sparingly; check `free_model_daily_requests`
  via `GET /api/v1/key` before spending tests. One agent step = one request.
- **Never write decrypted API keys or credentials to disk.** Load them in-process through `DatabaseService`.
- Don't test on Hybrid Local while the owner is using that model. Ask, or check that the server is idle with one tiny request first.
- **UI automation: never use SendKeys, simulated keyboard or mouse, or `SetForegroundWindow`.** It typed into the
  owner's other app once. Use only UI Automation patterns:
  - `ValuePattern.SetValue` on `QueryInput` (an AvalonEdit editor that supports it);
  - `InvokePattern` on `SendButton` / `StopButton`;
  - read chat cards through `TextPattern` on Document elements (AutomationId `WorkplaceCardContentText`);
  - launch the app with `-WindowStyle Minimized`; screenshot with `PrintWindow`, without restoring or activating the window.
- **Before UI-testing on the owner's profile,** back up `ChatHistory\workplace_session.json`,
  `workplace_advanced_state.json` and `agent_settings.json`, and restore them afterwards.
- Keep explanations plain and concise. The owner prefers one clear recommendation over a survey of options.

## 6. Subsystem notes and gotchas

### Cloud / OpenRouter transport (`OpenRouterChatService`)
1. **SSE keep-alives:** self-hosted gateways send `: keep-alive` comment lines while a tool call is being composed.
   Any non-`data:` SSE line must be skipped (`OllamaStreamChunkConverter.IsSseNonDataLine`). The old code treated
   them as a non-streamed JSON body and called `ReadToEndAsync`, which silently discarded the tool call. The body
   fallback now requires a line starting with `{`.
2. **Timeouts after headers:** `HttpClient.Timeout` does not apply once headers arrive (`ResponseHeadersRead`).
   Streams therefore have their own deadlines: line-idle, first content (8 min for custom-endpoint tool calls) and
   10 min total. Without these, stalled free-tier streams hung forever.
3. **Tool-call progress:** `onToolCallProgress` reports streaming tool-call argument size. On providers that only send
   a tool call once complete (Ollama-backed profiles), there is no progress until the end.
4. **Fallback chains (Normal chat only):** one delayed restart when the whole chain fails at once (`TryBeginFallbackChainRestartAsync`).
5. **Catalog and vision:** `SupportsImageInput` needs the model catalog, so call `EnsureModelCatalogAsync` first.
   The Workplace profile sets `KnownImageInput` for when the catalog hasn't loaded yet.
6. **Free models disappear:** Laguna M.1 left the free catalog. Inkling free returns 403 ("agentic harnesses only").
   Qwen3.8 27B free had a degraded single provider. Re-verify availability (the `/api/v1/models` endpoints API) before switching models.
7. **Leaked reasoning:** when thinking is off, send `reasoning:{enabled:false,exclude:true}`. Reasoning-by-default fallback
   models otherwise leak chain-of-thought into content.

### Python sandbox (`PythonExecutionService`)
8. `PythonEngine.Initialize()` must be followed by `BeginAllowThreads()`. Without it a pool thread owned the GIL forever,
   and the first `run_python` deadlocked the cloud tool loop ("generating" forever, GPU at 0%). GIL waits for session
   start and end are bounded (10 s). Tell-tale sign: no `MainWindow.PythonSandbox` log events at all.

### Normal chat tool loop and Project Canvas
9. Models tried to build HTML deliverables inside `run_python` and looped. The `run_python` description now says
   "computation only", duplicate calls are suppressed, and a forced no-tools synthesis runs after the grounding round on
   canvas/Skill turns, or after 3 sandbox runs.
10. **Canvas replies:** canvas artifacts must not render inside the chat bubble. The bubble shows a conversational
    `CanvasReplyText` (`ArtifactRenderService.BuildCanvasChatReply`), which is persisted and restored at 7 copy/restore
    sites; grep `CanvasReplyText` when adding one. A raw `<!DOCTYPE html>` reply is also adopted, but only with a
    line-anchored doctype/html plus head/body, so prose like "the `<html>` element" doesn't trigger it.
11. **Canvas fit:** the normalize script is injected into `<head>`, not body-end, so a truncated reply can't swallow it.
    It zooms to fit width with a floor of 0.3. Only `overflow: hidden/clip` counts as clipping; an author's `overflow-x:auto` wrapper doesn't.

### Workplace
12. **Notification roles:** `NotificationRoles` (`system`, `error`, `warning`, `memory`) go **only to the notification bell**,
    never the chat. Anything the user must see uses the `agent` or `notice` role (`AppendVisibleNotice`). "Already
    processing", @ComputerUse refusals and agent errors were all invisible before this.
13. **Timestamps:** Workplace card timestamps are card *creation* times, not completion times.
14. **Post-mortems:** TaskHistory entries keep raw `BuilderOutput` and `FinalResult`, which helps reconstruct a bad run.
15. **Codebase patch pipeline:** see the long history in commit messages. Short version:
    - never run cleanup, think-stripping or separator heuristics over bytes after `[[AXIOM_CODEBASE_PATCH]]` (the `=======` divider looks like a separator line);
    - SEARCH anchors from free models drift, so matching is whitespace-tolerant but must be unique, and a miss is retryable with exact file bytes;
    - every rescue path is fail-closed (no write beats a garbage write);
    - relevance check: a patch that doesn't touch the request is rejected (`TryDetectIrrelevantWorkspacePatch`).

12a. **Small local models and Project Canvas** (`SmallModelCanvasPlanner.cs`, V1.9.14): models under 4B
    (`SkillCanvasTier.Micro/Compact`) never author HTML. A deck, chart or @ProjectCanvas turn gets a short
    system prompt, plus a user turn holding the task, a worked example on an unrelated topic, and "Begin with
    TITLE:". Axiom composes the result (`SkillArtifactComposer`).
    - Follow-ups inherit the deliverable and topic of an earlier request.
    - History is dropped on these turns, because tiny models copy their own failed replies.
    - Measured on Qwen3-0.6B: placeholder templates (`<category> | <number>`) get copied literally, and the
      model heads slides with `TITLE:` and drops `CHART:` lines. The parsers accept those shapes. Re-test with
      the real GGUF (`C:\Users\user\Downloads\Qwen3-0.6B-tools-Q5_K_M.gguf`) through the harness
      `tinydeck` mode before changing these prompts.

### Agent Access (`Agent/`, `WorkplaceView.Agent.cs`)
16. **Native tool calling for Cloud and Hybrid:** both use the provider's tools through `CloudAgentModel`, never the
    council executor (which rewrites the system prompt and advertises other tools, making the model think it has no
    machine access). Local GGUF uses `TextProtocolAgentModel` (a JSON or flat text protocol, parsed back).
17. **Turns stream with output caps:** 16,384 tokens cloud, 8,192 Hybrid Local. A tool call cut off at the cap (broken JSON,
    or a server 500 "invalid tool call arguments … unexpected end of JSON") becomes "write it in parts" guidance plus
    `append_file`. Identical retries are pointless.
18. **`run_command`:** runs PowerShell with `-EncodedCommand` (UTF-16LE Base64) and `$ProgressPreference='SilentlyContinue'`.
    The old `-Command "…"` with backtick-escaped quotes mangled every quoted path. CLIXML stderr is converted to text.
    A failed bash-style command (`ls -la`, `2>/dev/null`, `&&`, `/mnt/f`) gets a PowerShell hint (`ShellMismatchHint`).
19. **Loop guards:** consecutive identical calls are blocked, plus a run-wide limit on exact repeats (`MaxIdenticalCallsPerRun = 2`,
    with read-only tools getting +2), which catches alternating rewrite/rerun cycles.
20. **Verifier:** `AgentWorkVerifier` checks the final summary against disk (claimed files that don't exist, HTML referencing
    missing local files) and sends the run back up to 2 times.
21. **Mid-run messages:** typing while the agent runs posts to `AgentUserMessageInbox`, delivered between steps as a `(user)`
    exchange. Send and Enter stay enabled; **there are two Enter handlers**, `QueryInput_KeyDown` and
    `WorkplaceQueryInput_PreviewKeyDown`, and both must allow it. A message sent with Stop starts the follow-up immediately.
    The run card splits so the chat reads in order.
22. **Backups:** `AgentFileBackup` copies any pre-existing file to `<profile>\AgentBackups\<task timestamp>\…` before its first
    change in a task, pruned after 14 days. This exists because an agent overwrote an unrelated project on `F:`.
    Prompt rule: new work goes in a new folder.
23. **Live run card:** the card shows the live activity and elapsed time, and explains Hybrid Local waits
    (`DescribeModelWait`). Never let a run sit on "Starting...".
24. **Continuation:** a follow-up message continues the active task (`AgentActiveTaskState`), unless it's a "new task" phrase.
    The follow-up overrides the original plan where they conflict.
25. **Context compaction:** older steps are compacted to summaries; only observations starting with `ERROR:` count as failures.
    *Open issue:* refused or blocked steps ("Blocked: …", "The user declined…") are still summarised as if they ran
    (e.g. "[Read X]").

### @ComputerUse
26. **Needs a vision model.** It uses the catalog check above.
27. **"Open / take me to X" must have X *in front*** (`IsRequestedApplicationInFront`). X being visible behind another
    app finished a run in 1 s having done nothing. Focus passing through the shell (explorer/desktop) counts as in front
    only while X is visible.
28. **Results in chat:** results and refusals appear as Agent or Notice cards in the chat (see #12).

### Updater
29. **Disconnected update drive:** when `AXIOM_UPDATE_DIR` points to a missing drive, `UpdateStoragePaths.Root` falls back
    to the profile folder. Downloads resolve a usable root *before* creating folders, and staging happens next to the
    downloaded ZIP. Don't set persistent env vars like `AXIOM_UPDATE_DIR` without the owner's approval.

### Local models (GGUF)
30. **Size classes:** `LocalModelSizeClass` (SubOneB / OneToFourB / FourToTenB / TenBPlus) scales tool trust, step budgets,
    prompt compaction and generation caps. Sub-1B models never get a model-chosen tool decision (they pick arbitrary tools
    and echo observations).
31. **Reasoning dialects:** `ReasoningParser` handles closing-only `</think>`, alias tags and GPT-OSS harmony channels.
    Reasoning models get extra generation headroom (`IsLikelyReasoningModel`).

### Editing pitfalls (tooling)
32. **Line endings:** files mix CRLF and LF, and `core.autocrlf=true` normalises on commit. Don't mass-convert.
33. **Bash heredocs:** the Bash tool turns `\\n` / `\r\n` inside heredoc Python into real newlines. Use the Edit tool, or
    write the script to a file first, for C# lines containing backslash escapes.
34. **PowerShell 5.1:** `Get-Content` without `-Encoding UTF8` mis-decodes UTF-8, and `Out-File`/`>` write UTF-16LE.

## 7. The owner's Hybrid Local server (context for diagnosing)

- **Endpoint:** `https://ai.axiominference.work`, a FastAPI control-plane proxy in front of Ollama / a llama.cpp build,
  managed by "Axiominference server control". Its own handoff doc is `YoMosa2009/Axiom-CLI/CLAUDE.md`.
- **Active profile:** usually Qwen3.6 35B-A3B UD-IQ3_S on llama.cpp: 131K context, about 31 tok/s, RTX 3060 12 GB.
  Other profiles: Gemma 4 12B, OmniCoder 2 9B, MiMo V2.6 9B (Ollama).
- **Tool-call streaming:** only the llama.cpp profile streams tool-call arguments. Ollama profiles emit a tool call only
  when it's complete, so long writes look silent; the run card explains this.
- **Cancel on Stop:** fixed server-side on 2026-10-01. Before that, a stopped request kept generating and every later request
  queued behind it (53 s versus 1.4 s for a tiny request). Re-check with a cancel probe if "Starting…" hangs return.
- **Token usage:** the stream reports no token usage, so the Workplace token meter doesn't update for Hybrid Local turns.

## 8. Testing recipes

- **In-process harness (live models without the UI):** a scratch console app that references `Malx_AI.dll`, run from a
  *copy of the app's bin folder* (the module initializer needs the WindowsAppRuntime DLLs) with `AXIOM_DATA_DIR` pointing
  at the owner's profile. It loads keys via `DatabaseService` in memory and drives `SendConversationStreamAsync` or a real
  `AgentSession` + `CloudAgentModel`. Useful probes:
  - cancel probe: time-to-first-token of a tiny request right after cancelling a long stream;
  - tool-stream probe: whether tool-call arguments stream progressively.
- **Verifying a fix:** reproduce from the owner's real logs and state first (`backend-events.log`, `workplace_session.json`),
  then confirm in the real app via the UIA-only recipe in §5, not just unit tests.

## 9. Delegating implementation to local Qwen (planner/implementer)

The owner's Qwen3.6 35B-A3B can implement tasks through **Axiom-CLI** (their OpenCode build):
`%LOCALAPPDATA%\axiom-cli\bin\axiom.exe code --yes --json "<task>"` (needs Bypass permissions in Claude Code).

- **Write very precise tasks:** exact file paths, which method or lines to change, the expected behaviour with examples,
  the exact *filtered* test command, and rules (allowed files only, no git, no installs). Symptom-only bug reports made it
  wander: 24 versus 10 tool calls, a wrong-subsystem detour, and an invented CLI flag.
- **Isolation:** always run in a separate git worktree on a `qwen-trial/*`-style branch, never `main`. Pre-build it, because
  a first build plus the full suite overruns OpenCode's 120 s shell timeout.
- **Monitoring:** watch the `--json` event stream. End the watch on the run's own exit, not on `axiom.exe` disappearing;
  the owner often has their own Axiom-CLI session open.
- **Review:** read the diff (its summaries misreport small details), run the full suite, tidy style (test names are
  PascalCase sentences with no underscores), then merge, push and remove the worktree.
- **Track record (2026-10-01):** fixed the agent line-count off-by-one (`CountLines`) and the compaction "Exception"
  misclassification. Both are correct, minimal and tested.

### Parallel offloading (owner-approved plan, 2026-10-01)

On multi-part jobs, Claude keeps the hard parts and hands medium-easy parts to Qwen in the background, then reviews
and merges them. Say up front which parts go to Qwen, and finish with one combined summary showing who did what.

- **Send Qwen:** contained single-file fixes, small refactors, tests for existing code, wiring a setting through,
  text and doc updates, simple UI property changes. Anything with a clear "done" test.
- **Keep:** debugging, async/threading, cross-file design, WPF layout, the codebase patch pipeline, anything risky or
  hard to verify.

Limits to plan around:
- **One Qwen task at a time.** The server handles one request at a time, so tasks queue rather than run in parallel.
  In practice that's one roughly 10-minute task in the background.
- **File overlap.** If Qwen and Claude edit the same file, merging gets messy. Only send Qwen tasks that touch files
  Claude isn't working on.
- **Same machine.** Its builds and tests compete for CPU and can lock build files. Separate worktrees avoid the locks;
  builds are just a bit slower while both run.
- **Review isn't free.** Each Qwen task costs a few minutes to check, so tiny tasks (under 5 minutes of Claude's time)
  aren't worth sending. The sweet spot is 15–30 minutes of routine work.
- **Quieter monitoring.** Don't react to every step event. Watch only for "finished" or "error", and check in at the end.
- **Permissions.** Launching `axiom code --yes` needs Bypass permissions, or a saved permission rule.

Expected gain: offloading one solid medium task per 10–15 minutes of Claude's work, not doubling speed.
