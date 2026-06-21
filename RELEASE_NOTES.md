# Release Notes — Axiom (Malx AI)

_Covering commits from the last 10 hours (2026-06-20 18:25 → 21:22)_

---

## 🖥️ UI/UX — Adaptive Resolution & Responsive Layout
**Commit:** `5cbfce6` — *Fixed screen resolution of the UI/UX formatting with an adaptive resolution checker*

- Added a new `MainWindow.ResponsiveLayout.cs` partial class implementing a full adaptive-layout system for the desktop window.
- **Initial window fit-to-monitor:** on startup the window now measures the current monitor's DPI-aware work area and sizes/centers itself to fit (94% of work area, capped at 1600×900), instead of risking an oversized window on smaller or differently-scaled displays.
- **Live breakpoints** introduced for three layout tiers:
  - Compact (`< 1280px`) — tighter padding, narrower sidebar (248px), smaller token usage panel.
  - Standard (1280–1700px) — default spacing, 280px sidebar.
  - Wide (`≥ 1700px`) — expanded padding/margins, wider sidebar (304px), larger token usage panel.
- Layout now re-applies automatically on `Loaded`, `SizeChanged`, and `StateChanged` (e.g. maximize/restore), keeping the navigation bar, chat header, chat display, input container, and settings panel correctly proportioned at any window size or DPI (100%/125%/150%/mixed-DPI tested logic).
- `WorkplaceView` now exposes `ApplyDesktopLayout(width)` so the Workplace/Council pane resizes in sync with the main window instead of independently.
- Minor follow-on edits to `MainWindow.xaml`, `MainWindow.xaml.cs`, `WorkplaceView.xaml`, and `WorkplaceView.xaml.cs` to wire the new responsive hooks in.

**Impact:** Fixes UI elements clipping, overflowing, or looking cramped/oversized on non-standard screen resolutions; the app now adapts cleanly across compact laptops, standard monitors, and ultrawide/high-res displays.

---

## 🤝 Council / Workplace Chat — Local & Cloud Mode Improvements
**Commit:** `c29a830` — *Improvements done to the Council/Workplace chat (local & cloud modes)*

Four files changed/added, ~600 lines of new logic across the multi-agent "Council" pipeline (Architect / Builder / Critic roles).

### New: `WorkplaceView.GoalContract.cs`
- Introduces a **Council Goal Contract** system (`CouncilGoalContract`) that formalizes each task into structured fields: `Goal`, `Deliverable`, `Requirements`, `Constraints`, `AcceptanceChecks`, `Assumptions`, `Environment`.
- Auto-generates **acceptance checks (R-items/C-items/A-items)** tailored to task type — coding, artifact/Canvas, document, research, analysis — e.g. enforcing "no TODO placeholders," "no CDN/remote/package dependency for Canvas artifacts," and "constraints satisfied without silent substitution."
- Gives every Council role (Architect, Builder, Critic) a single shared source of truth to plan against, build against, and audit against — reducing drift between what was asked, what was built, and what was verified.

### New: `WorkplaceView.CloudIntelligence.cs`
- Adds cloud-specific token budgeting: `GetCloudCouncilInputBudgetTokens()` reserves 32k tokens for system instructions/tool output/role output rather than assuming the full advertised context window is usable input.
- `ResolveCloudCouncilRoleMaxTokens()` sets per-role, per-complexity output caps (e.g. Builder up to 8192 tokens for complex/artifact/coding tasks, Architect capped lower at 3072–4096) — tuned to stay compatible with fallback models whose completion cap (8k) is smaller than their advertised context window.
- Adds a **Cloud Council Deliberation Protocol** prompt injected per-role, instructing models to treat the Task Contract as source of truth, rank user requirements above memory/documents/tool output, and avoid blending retrieved content with instructions.
- Adds an **independent verification packet** for the Critic role: a 5-step audit order (requirement coverage → constraint compliance → acceptance → execution reconciliation → completeness/truncation check), explicitly told not to reward verbosity/polish.
- Raises the cloud document character budget to 240,000 chars (~60k tokens) to make use of larger cloud context windows (e.g. 131k-token profiles) while leaving room for role instructions and generation.

### Reworked: `WorkplaceView.ToolDecision.cs` and `WorkplaceView.xaml.cs`
- Significant refactor (157 → restructured, and 253 lines changed in `xaml.cs`) integrating the new Goal Contract and Cloud Intelligence systems into the existing tool-decision and chat-orchestration flow for both local and cloud execution paths.

### Project file
- `Malx_AI.csproj` updated to include the two new source files in the build.

**Impact:** The Council pipeline now reasons with an explicit, auditable contract instead of implicit task understanding, and cloud-mode runs get smarter token-budget management plus stricter independent verification — reducing hallucinated "done" claims and improving consistency between Builder output and Critic review.

---

## 📄 Documentation — README Rewrite (Code-Verified)
**Commits:** `e2083af` (Claude) → `98b8586` / merged via PR #9 *Rewrite README with accurate, code-verified details*

Corrected inaccurate claims and added new code-grounded content:

**Fixes:**
- "Edios 1" → **"Eidos 1"** (matches actual code/UI spelling).
- Eidos 1 / Hepha 1 model details corrected to real OpenRouter model IDs and fallback chains (**Gemma 4 26B** / **Nemotron 3 Super 120B**), replacing previously incorrect GPT-OSS/Kimi K2 claims.
- Sandbox execution scope corrected to **Python and Java only** (README previously over-claimed JavaScript, PowerShell, and C# execution support that doesn't exist in code).
- "LLaMA.net" → **LLamaSharp** (the actual NuGet package in use).
- Default local model corrected to **Axiom Qwen3-4B (GGUF)**.

**New sections:**
- **AI Pipeline & Tools** — documents tool agents (Calculator, Python sandbox, Java sandbox, Web Search), the Council's agentic tool loop, and the Critic contract.
- **Memory/context systems** — Session Hippocampus, Persona Memory, Smart Context Compaction.
- **Models** section with a fallback-chain table for cloud aliases.
- **Document attachment support** — 50+ text/code formats plus PDF, DOCX, XLSX, and images.
- **Expanded Built With** — Markdig, HtmlAgilityPack, AvalonEdit, PdfPig, SQLite, CUDA 12 backend.

**Impact:** README now accurately reflects the shipped feature set, preventing user/developer confusion about model identity, sandbox capabilities, and dependencies.

---

## Summary

| Area | Files Touched | Lines Changed | Highlights |
|---|---|---|---|
| Responsive UI layout | 5 | +236 / −11 | Adaptive monitor-fit, 3-tier breakpoints, Workplace sync |
| Council/Workplace (local+cloud) | 5 | +609 / −125 | Goal Contract system, cloud token budgeting, verification packet |
| README | 1 | rewrite | Model IDs, sandbox scope, dependencies, new feature docs |

**Net effect of the last 10 hours:** the desktop UI is now display-size aware, the Council multi-agent pipeline gained a formal contract/verification layer for both local and cloud execution, and the README was brought back in line with the actual codebase.
