# Axiom roadmap and work tracker

What has shipped, what is waiting on the owner, and what comes next. Update this file in the
same commit as the work it describes, so nothing agreed in a conversation gets lost.
`CHANGELOG.md` is the user-facing history; this file is the working list.

## Waiting on the owner

| Item | What to do | Why it matters |
| --- | --- | --- |
| **Feedback: direct sending** | Go to [web3forms.com](https://web3forms.com), enter `Malxshrouds@gmail.com`, and copy the access key they email you into `FeedbackDelivery.Web3FormsAccessKey` (or hand it to Claude). The key is designed to be public: it can only send mail to that one inbox. Free tier: 250 messages a month. Then test one real send, because Web3Forms documents browser use and a desktop app's request should be confirmed once. | Until then **Send feedback** opens the user's email app with the report filled in, which fails quietly for people with no mail app set up. |
| **Code signing** (deferred, owner decision 2026-10-09) | When the user base grows: Azure Trusted Signing (about $10/month, identity validation required), then sign `Malx_AI.exe` and the release ZIP's executables in `release.yml`. | Removes the "Windows protected your PC" SmartScreen warning on first launch. Until then README's Getting started explains **More info → Run anyway**. |
| **CI release secrets** | Confirmed missing on 2026-10-09 (the V1.10.0 CI run stopped before publishing). In GitHub: repo **Settings → Secrets and variables → Actions → New repository secret**, add `AXIOM_BUILTIN_GOOGLE_CLIENT_ID`, `AXIOM_BUILTIN_GOOGLE_CLIENT_SECRET`, `AXIOM_BUILTIN_GITHUB_CLIENT_ID`, `AXIOM_BUILTIN_TODOIST_CLIENT_ID`, `AXIOM_BUILTIN_TODOIST_CLIENT_SECRET` with the same values as the local OAuth files. | Until then every release has to be published from this PC with `scripts/Publish-GitHubRelease.ps1`. |

## Shipped

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
