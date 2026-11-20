# Axiom Implementation & Verification Tracker

Last updated: 2026-11-17
Repository: [YoMosa2009/Axiom](https://github.com/YoMosa2009/Axiom)

## Current source status

The owner authorized committing and synchronizing the recent source and docs to
GitHub on 2026-11-17. This does not authorize a release. The app version stays
1.11.0; GitHub's latest published release was checked as V1.11.0 (2026-10-10).

| Commit | Completed follow-up | Evidence and limits |
| --- | --- | --- |
| `c370e03` | Responsive Workplace layout, durable conversation/tool evidence, Computer Use continuity and passive screenshots | [Layout/context](docs/WORKPLACE_LAYOUT_CONTEXT_20261116.md), [capture](docs/WORKPLACE_CAPTURE_FOLLOWUP_20261116.md) |
| `a8d02cb` | Native Windows controls, responsive chat/composer widths, simplified tool controls | [Layout/composer](docs/UI_LAYOUT_COMPOSER_20261117.md) |
| `5ede4a6` | Context typography/spacing and duplicate sidebar toggle removal | [Context sidebar](docs/CONTEXT_SIDEBAR_20261117.md) |
| `cf58509` | Cloud daily quota percentage and theme fill; navigation, profile isolation and atomic-save fixes | [Usage/testing](docs/CLOUD_USAGE_STRESS_TEST_20261117.md) |
| `83b49cc` | Shell EOF false-timeout fix, raw evidence/replay handling, shared spinner, themed activity dropdown, background notification audio, Canvas redesign | [Run experience](docs/WORKPLACE_RUN_EXPERIENCE_20261117.md) |
| `7b6ff94` | Reference-style completion changes panel, recorded counts, expandable rows, captured diff review, history persistence and latest Agent Undo | [Change summary](docs/WORKPLACE_CHANGE_SUMMARY_20261117.md) |

## Latest verification (2026-11-17)

- Full portable suite after `7b6ff94` implementation: **954 passed, 0 failed, 0 skipped**, 58 seconds.
- WPF Debug build: **0 errors**, 352 existing warnings.
- Final change-summary private-desktop fixture: **19 checks passed**, including
  three/all file rows, actual counts, captured review, snapshot round trip,
  narrow/theme layouts, applied Council patches, Canvas and real owned-file Undo.
- Prior `83b49cc` acceptance: **18 progress/activity checks** and **24 Canvas checks**
  passed. These are separate recorded passes, not an aggregate live stress-test score.
- Earlier cloud/user-flow pass: 929 tests and 19 usage-render checks; full scenario
  counts and the unverified native picker flow are in its linked report above.
- Graphify was refreshed after implementation; `git diff --check` passed.

## Remaining acceptance and scope limits

- These tests do not establish general model reliability. The owner confirmed the
  latest generated photo editor worked; the harness fixes address reproduced issues.
- Native toast delivery and audible notification sound were not exercised on the
  owner's desktop. Eligibility and SDK sound configuration were checked.
- A completion panel reports recorded file-tool/applied patch edits and Canvas
  changes. Arbitrary shell/background/desktop edits are not inferred or counted.
  Binary/unreadable text counts are unknown, not fabricated. Restored chats retain
  captured review but do not revive in-memory Undo authority.
- Native WebView preview content was checked through its DOM; WPF bitmaps exclude
  HWND-hosted pixels. The native attachment picker still lacks end-to-end acceptance.
- Models have finite prompt windows. Full retained evidence can be recalled; this
  does not reconstruct raw output discarded by older app versions.
- No version bump, release build, public asset upload or OTA publication is part of
  this synchronization. Live generation/OAuth and broader desktop scenarios retain
  the limits described in the dated reports.

## Historical feature checklist (20 / 20, recorded 2026-11-16)

The original checklist and verification log below are preserved as history. Their
per-item claims were not all rerun during the 2026-11-17 documentation update; use
the current results and linked reports above for the latest verified scope.

- [x] **1. `@ComputerUse` multi-step planning & execution reliability**
  - Files: `Malx_AI/ComputerUse/ComputerUseDeterministicPlanBuilder.cs`, `Malx_AI/ComputerUse/ComputerUseTaskContract.cs`, `Malx_AI/ComputerUse/ComputerUseSessionController.cs`, `Malx_AI/ComputerUse/ComputerUseNativeInput.cs`, `Malx_AI/WorkplaceView.ComputerUse.cs`, `Malx_AI.Tests/ComputerUseDeterministicPlanTests.cs`
  - Verified: 273 `@ComputerUse` tests passed; included in full 772/772 test run.

- [x] **2. Compact global font & control sizing across Chat & Workplace**
  - Files: `Malx_AI/App.xaml`, `Malx_AI/RenderedMessageView.xaml`, `Malx_AI/MessageHtmlRenderer.cs`, `Malx_AI/WorkplaceMarkdownDocumentFactory.cs`, `Malx_AI/MainWindow.xaml`, `Malx_AI/WorkplaceView.xaml`
  - Verified: Clean Debug build + live UIA launch & control bounds verified in `Malx_AI.exe`.

- [x] **3. Background task execution & pause/wake resumption across all model tiers (sub-1B to 10B+)**
  - Files: `Malx_AI/Agent/AgentBackgroundTaskManager.cs`, `Malx_AI/Agent/AgentTool.cs`, `Malx_AI/Agent/AgentToolSchemas.cs`, `Malx_AI/Agent/AgentToolExecutor.cs`, `Malx_AI/Agent/AgentSession.cs`, `Malx_AI/Agent/AgentPromptBuilder.cs`, `Malx_AI.Tests/AgentBackgroundTaskTests.cs`
  - Verified: Concurrent `wait=false` and blocking `wait=true` + wake resumption covered by `AgentBackgroundTaskTests`.

- [x] **4. Workplace `+` -> `Select Project` popup & strict folder confinement**
  - Files: `Malx_AI/WorkplaceView.xaml` (`WorkplacePlusPopup`, `SelectProjectPopup`), `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI/Agent/AgentPermissionPolicy.cs`, `Malx_AI/Agent/CommandSafetyReviewer.cs`, `Malx_AI/Agent/AgentToolExecutor.cs`
  - Verified: Live UIA verified `WorkplaceAttachFileButton` -> `Select Project` -> `Choose Folder...`; path confinement & sibling-prefix blocking covered in `CommandSafetyReviewerTests`.

- [x] **5. Right Sidebar Diff toggle & Context / Environment inspector (Images 1, 2, 3)**
  - Files: `Malx_AI/WorkplaceView.xaml` (`WorkplaceDiffSidebarButton`, `WorkplaceContextSidebarButton`, `ContextEnvironmentPanel`, `DiffViewerScroller`), `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI/LineDiff.cs`
  - Verified: Live UIA verified toggling `WorkplaceDiffSidebarButton` (Session changes) and `WorkplaceContextSidebarButton` (`Environment`, `Context`, `Compact session`).

- [x] **6. Right Sidebar `Background tasks` section with live elapsed timer & stop control (Image 4)**
  - Files: `Malx_AI/WorkplaceView.xaml` (`RightSidebarBackgroundTasksPanel`), `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI/Agent/AgentBackgroundTaskManager.cs`
  - Verified: Clean build & live UIA verified `Background tasks` section inside Context & Environment right sidebar.

- [x] **7. Automatic Workplace session naming & user rename editing**
  - Files: `Malx_AI/WorkplaceSessionTitleGenerator.cs`, `Malx_AI/WorkplaceSessionPersistence.cs`, `Malx_AI/WorkplaceView.xaml` (`WorkplaceSessionTitleButton`), `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI.Tests/WorkplaceSessionTitleGeneratorTests.cs`
  - Verified: Covered by `WorkplaceSessionTitleGeneratorTests` + live UIA presence of `WorkplaceSessionTitleButton`.

- [x] **8. Left Sidebar `Projects` + `Conversations` tree with folder grouping & relative ages (Image 5)**
  - Files: `Malx_AI/SidebarHistoryGroups.cs`, `Malx_AI/MainWindow.xaml`, `Malx_AI/MainWindow.Sidebar.cs`, `Malx_AI/WorkplaceView.xaml`, `Malx_AI/WorkplaceView.Overhaul.cs`
  - Verified: Clean build & live UIA startup (`Axiom - Chat`).

- [x] **9. Mandatory agent `.md` task tracker file per project + post-compaction context preservation**
  - Files: `Malx_AI/Agent/WorkplaceTaskTrackerFileService.cs`, `Malx_AI/Agent/AgentPromptBuilder.cs`, `Malx_AI/Agent/AgentSession.cs`, `Malx_AI/WorkplaceView.Agent.cs`, `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI.Tests/WorkplaceTaskTrackerFileTests.cs`
  - Verified: Covered by `WorkplaceTaskTrackerFileTests` (`ResolveTrackerPath`, `InitializeOrUpdateTracker`, `ReadTrackerForCompactionContext`).

- [x] **10. Coherent task-tailored output presentation in Normal Chat & Workplace**
  - Files: `Malx_AI/MainWindow.xaml`, `Malx_AI/WorkplaceView.xaml`, `Malx_AI/WorkplaceView.SingleModel.cs`, `Malx_AI/WorkplaceView.Agent.cs`, `Malx_AI/MessageHtmlRenderer.cs`
  - Verified: Clean Debug build & full suite pass.

- [x] **11. Concise inline status updates (`<status> <Ns> ›`) for all model sizes (Image 6)**
  - Files: `Malx_AI/MainWindow.ToolAgents.cs`, `Malx_AI/MainWindow.Inference.cs`, `Malx_AI/WorkplaceView.xaml` (`AgentActivityLine`), `Malx_AI/WorkplaceView.Agent.cs`
  - Verified: Clean Debug build & runtime startup without UI errors.

- [x] **12. Native single-browser detection & web control tool in Workplace**
  - Files: `Malx_AI/Agent/BrowserControlService.cs`, `Malx_AI/Agent/AgentTool.cs`, `Malx_AI/Agent/AgentToolSchemas.cs`, `Malx_AI/Agent/AgentToolExecutor.cs`, `Malx_AI/Agent/AgentPermissionPolicy.cs`, `Malx_AI.Tests/BrowserControlServiceTests.cs`
  - Verified: Covered by `BrowserControlServiceTests`.

- [x] **13. Workplace Command Review selector (`Auto Review`, `Manual Review`, `Auto Approve`) & interception**
  - Files: `Malx_AI/Agent/CommandSafetyReviewer.cs`, `Malx_AI/WorkplaceView.xaml` (`CommandReviewSelectorButton`, `CommandReviewPopup`), `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI/WorkplaceView.Agent.cs`, `Malx_AI.Tests/CommandSafetyReviewerTests.cs`
  - Verified: Live UIA verified `Auto Review`, `Manual Review`, and `Auto Approve` popup options; unit tests cover safe/risky/encoded/quoted-path interception.

- [x] **14. Model-chosen `wake_seconds` sleep/wake interval on background tasks**
  - Files: `Malx_AI/Agent/AgentBackgroundTaskManager.cs`, `Malx_AI/Agent/AgentToolSchemas.cs`, `Malx_AI/Agent/AgentToolExecutor.cs`, `Malx_AI/Agent/AgentSession.cs`, `Malx_AI.Tests/AgentBackgroundTaskTests.cs`
  - Verified: Covered by `WaitForTaskOrWakeTimer_WakesAtRequestedDurationBeforeLongTaskFinishes` and `AgentSession_InjectsBackgroundTaskWakeAndContinuesOtherWork`.

- [x] **15. Settings -> General -> `Your Profile` (editable `Your Name`, Lifetime tokens, Streaks, 52x7 theme-colored Heatmap, Image 7)**
  - Files: `Malx_AI/UserProfileStatsService.cs`, `Malx_AI/GenerationTelemetry.cs`, `Malx_AI/MainWindow.xaml` (`OpenYourProfileButton`, `SettingsPageProfile`), `Malx_AI/MainWindow.xaml.cs`, `Malx_AI/MainWindow.Theme.cs`, `Malx_AI.Tests/UserProfileStatsTests.cs`
  - Verified: Covered by `UserProfileStatsTests` + live UIA verification of Profile screen (`Your Name`, `0`, `0 days`, `Daily`/`Weekly`/`Cumulative` toggles).

- [x] **16. Redesigned `EffortSelector` button & card popover**
  - Files: `Malx_AI/EffortSelector.xaml`, `Malx_AI/EffortSelector.xaml.cs`
  - Verified: Clean Debug build + present in live Workplace UIA tree.

- [x] **17. Complete removal of `Neuron` page & navigation**
  - Files: `Malx_AI/MainWindow.xaml`, `Malx_AI/MainWindow.xaml.cs`, `Malx_AI/MainWindow.Background.cs`
  - Verified: Live UIA confirmed `Chat=True Workplace=True Persona=True Neuron=False`.

- [x] **18. Hydrogen (`flash_lite`) brand-new-user QA & live UI/integration bug fixes**
  - QA Subagent: `axiom-new-user-qa` (`Model="flash_lite"`, isolated `AXIOM_DATA_DIR`, local mock OpenAI HTTP server) + live WPF UIA probes & `Malx_AI.Tests`.
  - Critical/Medium bugs caught & fixed during QA:
    1. Fixed startup crash (`InvalidOperationException: Operation is not valid while ItemsSource is in use` in `ClearCanvasDiff()`) by keeping `DiffViewerLines` in `Items` collection mode in `WorkplaceView.Overhaul.cs`.
    2. Fixed parallel test interference in `Malx_AI.Tests/AgentBackgroundTaskTests.cs` by joining `[Collection("ProcessLifecycleCollection")]`.
    3. Fixed `CommandSafetyReviewer` (`Malx_AI/Agent/CommandSafetyReviewer.cs`) so quoted project paths with spaces are not falsely blocked and `Invoke-Expression`/`iex`/`-EncodedCommand` are intercepted in `AutoReview`.

- [x] **19. Removal of `Architect` + streamlined Workplace UI (`WorkplaceConfigPopup`, Extra High / Ultra Builder+Critic without Architect rules, Images 8–9 & 13)**
  - Files: `Malx_AI/WorkplaceView.xaml`, `Malx_AI/WorkplaceView.Overhaul.cs`, `Malx_AI/WorkplaceView.SingleModel.cs`, `Malx_AI/WorkplaceView.xaml.cs`
  - Verified: Live UIA confirmed `ARCHITECT_CONTROLS_COUNT: 0 []` in Workplace.

- [x] **20. Borderless assistant chat bubbles, right-aligned user pills & removal of normal-chat bottom clutter (Images 11–12)**
  - Files: `Malx_AI/MainWindow.xaml`, `Malx_AI/MessageHtmlRenderer.cs`, `Malx_AI/WorkplaceView.xaml`
  - Verified: Clean Debug build (`0 Error(s)`) & live app startup.

- [x] **21. Managed Cloud Subscription (`DeepSeek 4.1F-1017`), Settings `Usage` Pacing, Modal Click-Through Fix, Multi-Format Project Canvas, Autonomous `Auto-Review` & Chat Typography**
  - Files:
    - Gateway: `tools/AxiomSubscriptionGateway/AllowanceLedger.cs`, `AccountGateway.cs`, `LocalGateway.cs`, `OpenRouterInference.cs`
    - Managed Cloud & Workplace: `Malx_AI/OpenRouterChatService.cs`, `Malx_AI/MainWindow.Cloud.cs`, `Malx_AI/MainWindow.Subscription.cs`, `Malx_AI/WorkplaceView.xaml`, `Malx_AI/WorkplaceView.xaml.cs`, `Malx_AI/WorkplaceView.ComputerUse.cs`, `Malx_AI/WorkplaceView.Agent.cs`
    - Settings `Usage` & Modal Fix: `Malx_AI/MainWindow.xaml`, `Malx_AI/MainWindow.xaml.cs`, `Malx_AI/WorkplaceView.Overhaul.cs`
    - Project Canvas & Links: `Malx_AI/CanvasFileArtifact.cs`, `Malx_AI/WorkplaceChatMessage.cs`, `Malx_AI/WorkplaceMarkdownDocumentFactory.cs`, `Malx_AI/WorkplaceChangeSummary.xaml`, `Malx_AI/WorkplaceView.CanvasPresentation.cs`, `Malx_AI/WorkplaceView.ChangeSummary.cs`, `Malx_AI/MainWindow.ProjectCanvas.cs`, `Malx_AI/Agent/AgentPromptBuilder.cs`
    - Command Review & Typography: `Malx_AI/Agent/CommandSafetyReviewer.cs`, `Malx_AI/RenderedMessageView.xaml`, `Malx_AI/MessageHtmlRenderer.cs`
    - Tests: `Malx_AI.Tests/AxiomAccountGatewayTests.cs`, `ManagedInferenceGatewayTests.cs`, `CanvasFileArtifactTests.cs`, `CommandSafetyReviewerTests.cs`, `BrowserControlServiceTests.cs`
  - Verified (2026-11-18):
    - `dotnet test Malx_AI.Tests/Malx_AI.Tests.csproj -c Release --no-restore`: `Passed: 993, Failed: 0, Skipped: 0, Total: 993`
    - `dotnet build Malx_AI/Malx_AI.csproj -c Release -r win-x64 --no-restore`: `0 Error(s)` (`352` warnings); Debug `win-x64` build: `0 Error(s)`
    - Hosted staging gateway updated and verified against an isolated test environment with idempotent SQLite ledger migration intact.

- [x] **22. Managed Gateway Output/Reasoning Flexibility, Dynamic Reservations, Commercial License Clarity & Prompt-Cache Prefix Stability**
  - Files:
    - Gateway: `tools/AxiomSubscriptionGateway/OpenRouterInference.cs`
    - Client & Prompts: `Malx_AI/OpenRouterChatService.cs`, `Malx_AI/MainWindow.Cloud.cs`, `Malx_AI/WorkplaceView.xaml.cs`, `Malx_AI/WorkplaceView.Agent.cs`, `Malx_AI/Agent/AgentPromptBuilder.cs`, `Malx_AI/Agent/CloudAgentModel.cs`
    - Docs & Hygiene: `README.md`, `CONTRIBUTING.md`, `.github/pull_request_template.md`, `.gitignore`, `CHANGELOG.md`
    - Tests: `Malx_AI.Tests/ManagedInferenceGatewayTests.cs`
  - Verified (2026-11-19):
    - `dotnet test Malx_AI.Tests/Malx_AI.Tests.csproj -c Release --no-restore`: `Passed: 996, Failed: 0, Skipped: 0, Total: 996`
    - `dotnet build Malx_AI/Malx_AI.csproj -c Release -r win-x64 --no-restore`: `0 Error(s)` (`352` warnings); Debug `win-x64` build: `0 Error(s)` (`352` warnings)
    - Hosted gateway package republished (`win-x64`, Release) and verified healthy against an isolated staging environment.

- [x] **23. Automated Code Signing (Azure Artifact Signing OIDC) & WinGet Publishing**
  - Files: `.github/workflows/release.yml`, `.github/workflows/winget.yml`, `scripts/Publish-CleanRelease.ps1`, `scripts/Publish-GitHubRelease.ps1`, `scripts/Submit-WingetRelease.ps1`, `RELEASING.md`, `CHANGELOG.md`
  - Verified (2026-11-20): `axiom-public-trust` (`PublicTrust`, `Active`) provisioned and all six `AZURE_*` GitHub Actions secrets configured; PowerShell hashtable splatting (`@params`) fix applied across all `Publish-GitHubRelease.ps1` invocations in `release.yml`.

- [x] **24. Production Subscription Service, Direct Upstream Provider Switch, Legal Pages & UI/UX Screenshot Audit**
  - Files:
    - Gateway: `tools/AxiomSubscriptionGateway/AllowanceLedger.cs`, `AxiomAccountStore.cs`, `StripeTestCheckout.cs`, `StripeTestWebhook.cs`, `AccountDatabaseBackup.cs`, `OpenRouterInference.cs`, `LocalGateway.cs`, `AccountGateway.cs`, `Program.cs`
    - Client & Onboarding: `Malx_AI/AxiomAccountBuiltIns.cs`, `AxiomAccountClient.cs`, `OpenRouterChatService.cs`, `CloudUpgradeSuggestionPolicy.cs`, `MainWindow.xaml`, `MainWindow.Subscription.cs`, `MainWindow.xaml.cs`, `MainWindow.Inference.cs`, `MainWindow.Mcp.cs`, `WorkplaceView.ComputerUse.cs`, `FirstRunGuideWindow.xaml`, `FirstRunGuideWindow.xaml.cs`
    - Build & Legal: `TERMS.md`, `PRIVACY.md`, `scripts/Generate-McpOAuthBuiltIns.ps1`, `.gitignore`, `.github/workflows/release.yml`
    - Tests: `Malx_AI.Tests/Malx_AI.Tests.csproj`, `Malx_AI.Tests/SubscriptionProductionReadinessTests.cs`
  - Verified (2026-11-20):
    - UI/UX screenshot pass rendered across First-Run (`720x650`), Chat + Project Canvas (`1366x823`), Workplace, Persona, and all five Settings pages (`Inference`, `Cloud AI`, `Connectors`, `General`, `Usage`) plus live `Malx_AI.exe` startup smoke check. Restored Normal Chat `ProjectCanvasToggleButton` visibility inside `UnifiedTitleBar`, added `ChooseAxiomCloudButton` (`$9/mo`) to `FirstRunGuideWindow`, and updated `MainWindow.Mcp.cs` connector hint text.
    - `dotnet test Malx_AI.Tests/Malx_AI.Tests.csproj -c Release`: `Passed: 1005, Failed: 0, Skipped: 0, Total: 1005`.

## Historical verification log (2026-11-16)
- `dotnet build C:\Users\user\source\repos\Axiom\Malx_AI\Malx_AI.csproj -c Debug --no-restore`: `0 Error(s)`
- `dotnet test C:\Users\user\source\repos\Axiom\Malx_AI.Tests\Malx_AI.Tests.csproj --no-restore`: `Passed: 772, Failed: 0, Skipped: 0, Total: 772`
- Live WPF UIA verification on `Malx_AI.exe` (`AXIOM_DATA_DIR` isolated to `%TEMP%`): clean boot to `Axiom - Chat`, verified Profile, Workplace right-sidebar toggles, `+` -> `Select Project`, `CommandReview` popup, zero Neuron tab, zero Architect controls, clean logs.
