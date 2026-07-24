# Copilot Instructions

## Project Guidelines
- User wants workplace AI responses to execute requested tasks directly and provide final outputs, not default to tutorial/how-to guidance unless explicitly requested.
- For GPU integration changes, prioritize non-conflicting implementation that does not interfere with existing app behavior, with safe fallback behavior. Defer GPU troubleshooting for now and prioritize other development tasks until the user asks to revisit GPU issues.
- User wants the assistant to extensively review and thoroughly analyze the app to build deep project understanding before and during future updates.
- For Council pipeline internals, enforce strict per-role output contracts with termination markers, role-boundary drift checks with one correction rerun, deterministic compressed pipeline state headers, completed-steps registry with loop detection, and explicit pipeline health signals for Critic; do not alter normal chat mode or unrelated UI/features.
- For Council research/analysis summarization of attached files, Architect/Builder must treat uploaded document text as already ingested in PROJECT KNOWLEDGE BASE and must not output external file-operation steps like opening files or OCR.
- In Council/Workplace chat, the Architect role must only produce planning output and must never generate or build code; implementation belongs exclusively to the Builder role.
- User prefers all inference to run locally on-device and does not want API-based model calls for chat systems.
- When Qwen3 thinking mode is off, enforce strict direct-response behavior with no exposed reasoning text to conserve tokens and context.
- User expects published app behavior to be validated carefully for regressions, especially LaTeX rendering and visible reasoning content in normal chat.
- User prefers OpenRouter as the next cloud provider target because it offers many models, including good free options, and wants future agent-mode work aligned to that direction.
- User wants OpenRouter cloud mode to always prefer Nvidia Nemotron 3 Super (free) as the selected model.
- User wants OpenRouter normal chat cloud mode to expose exactly two user-switchable aliases: Eidos 1 for GPT-OSS 20B Free (thinking) and Hepha 1 for a free coding-focused OpenRouter model, with thinking controlled via the OpenRouter reasoning parameter instead of prompt tokens, and Gemini services removed from the app. User wants these model options visible in normal chat again.
- User may explicitly switch out of AGENT mode and ask for explanation-only responses without generated code; comply when stated.
- For Kestrel 1 in hybrid-local mode with Codebase Access, retain the full available tool catalog; do not restrict it to read-only codebase inspection tools. File creation and edits are performed through the returned patch envelope.
- For Kestrel 1 in hybrid-local Council/Workplace chat, the Builder must stop tool/planning loops and return a complete requested implementation; it must not emit Architect-style plans or end a turn without a deliverable.

## UI Design Preferences
- For Axiom UI refinements, prefer a modern, clean, easy-to-navigate palette with the following color scheme:
  - Primary background: `#6c685b`
  - Surfaces: `#d5dad3`
  - Chat/menu backgrounds: `#3b2c24`
  - Borders: `#2D3139`
  - Primary text: `#D1D3DF`
  - Secondary text: `#9CA3AF`
  - Key actions: `#FF3B3B`
- User wants stronger UI contrast: chat backgrounds, menus, buttons, and interactive surfaces should use `#D5DAD3` against darker backgrounds so navigation is easier and the app does not look monochromatic.
- User wants the current taupe theme reversed into a darker presentation so the UI does not look like light mode and remains easier on the eyes.
- User prefers a larger, cleaner dark orange inline loading spinner near Stop/Send in normal chat, without blur effects, and wants smooth non-blur chunked text streaming.
- User wants the Next model label/dropdown removed, and the Theme label and Dark Mode button removed from Settings. In workplace cloud mode, hide manual Role Context Controls and rely on the cloud model's own context handling. User also wants the Settings panel to be globally accessible across all app views, and workplace chat deletions to persist permanently across restarts and not reappear.
- User prefers the Neuron view to be visually detailed and smooth (not choppy), and wants quick copy buttons on AI outputs in both normal and workplace chats. User wants the left-sidebar Persona Memory button removed while keeping Persona tab functionality.
- User wants the normal chat empty-state logo to exactly match the provided app logo design, with a transparent background/light regions, and the main logo/frame rendered white for visibility, matching the source image as closely as possible. The logo should be about 25% smaller than the previous version and use straight lines while keeping the circles.
- User wants the normal chat greeting text visible on new chat/startup.