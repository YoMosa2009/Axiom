# Axiom

A free, local-first AI assistant and agentic workspace for Windows. Axiom supports
on-device GGUF models, self-hosted OpenAI-compatible endpoints, and optional cloud
models through OpenRouter. Local conversations and application data remain on your
computer unless you deliberately use a cloud model or connected service.

![License](https://img.shields.io/badge/license-CC%20BY--NC--ND%204.0-lightgrey)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Release](https://img.shields.io/badge/release-V1.8.5-brightgreen)
![.NET](https://img.shields.io/badge/.NET-10-purple)

<img width="1906" height="1026" alt="Axiom screenshot" src="https://github.com/user-attachments/assets/07bbb46d-1bc8-42d7-a16c-5912d2f874d8" />

## Highlights

- Three inference paths: local GGUF, Hybrid Local through a self-hosted endpoint,
  and optional OpenRouter cloud models
- Normal Chat with tool use, attachments, vision input, persistent conversations,
  web research, code execution, math, and artifact rendering
- Workplace Council mode with Architect, Builder, and Critic stages
- Workplace Single Model mode for one agent that plans, uses tools, executes, and
  verifies without role handoffs
- Global Skills and Plugins that attach once and apply across Local, Cloud, Hybrid
  Local, Council, and Single Model modes
- In-app updates from stable GitHub Releases starting with V1.7.0
- Optional low-resource system-tray operation with explicit background controls
- Local persistence for settings, chats, connectors, models, and Workplace state

## Chat and Workplace

### Normal Chat

- Import and run GGUF models through LLamaSharp, with model-aware context and tool
  routing for small and large local models
- Use Edios 1.5 or Hepha 2.5 Coder through OpenRouter with your own API key
- Connect to a self-hosted OpenAI-compatible endpoint through Hybrid Local mode
- Attach documents, spreadsheets, presentations, e-books, notebooks, source code,
  subtitles, and common image formats
- Run Python, calculate expressions and unit conversions, and search the web when
  the selected mode exposes those tools; Java execution is available for explicit
  Java tasks in Project Canvas and supported Workplace flows
- Type `@ProjectCanvas` or select **Project Canvas** from the `@` menu to render a
  completed Markdown, SVG, HTML, or interactive JavaScript artifact in a responsive
  right-hand canvas across Local, Hybrid Local, and Cloud modes
- Render Markdown and LaTeX stably while scrolling through long conversations
- Preserve chat history, persona memory, and document retrieval context locally

### Workplace

**Council mode** uses a three-stage pipeline:

1. Architect plans the task.
2. Builder creates the implementation or deliverable.
3. Critic reviews the result and can route it back for a targeted patch or revision.

**Single Model mode** replaces those role handoffs with one agent. The agent receives
the same Workplace context and can use the applicable tools, connected workspace,
Project Canvas, attachments, and session memory while planning and validating its own
result. The mode can be switched directly from the Workplace header when no run is active.

Workplace also includes persistent sessions, study/document preprocessing, codebase
access, diff-aware review, task history, context controls, live activity, completion
notifications, and an offline Project Canvas for self-contained HTML and SVG output.

## Skills and Plugins

The Normal Chat composer includes animated **Skills** and **Plugins** panels. Attached
capabilities are stored locally and apply globally instead of being tied to one chat,
model, or inference mode.

Built-in Skills:

- PDF Studio
- Slide Deck Studio
- Document Summarizer
- Data Analysis
- Code Review

Users can also create their own instruction-based Skills. Custom Skills provide reusable
procedures and activation terms; they do not execute arbitrary scripts.

Built-in Plugins package capabilities already provided by Axiom:

- Web Research
- Data Lab
- File Intelligence
- Connected Apps
- Creator Studio

Plugins never grant capabilities that the current model or host does not expose. Connected
Apps use only the MCP connectors configured in Settings.

## Models

Local mode accepts compatible GGUF models and includes in-app model installation. Cloud
mode uses OpenRouter and your own API key.

| Axiom profile | Primary model | Intended use |
|---|---|---|
| **Edios 1.5** | Google Gemma 4 31B (free) | General chat, reasoning, documents, and tool use |
| **Hepha 2.5 Coder** | NVIDIA Nemotron 3 Ultra (free) | Repository-aware coding and implementation |
| **Workplace cloud default** | Poolside Laguna M.1 (free) | Council or Single Model Workplace tasks |
| **Kestral 1** | User-configured self-hosted endpoint | Hybrid Local inference |

Cloud availability, quotas, and model routing depend on OpenRouter and its providers.
Axiom reports exhausted keys and transient rate limits rather than silently presenting
them as successful responses.

## Tools, memory, and privacy

- **Calculator:** scientific expressions and common unit conversions
- **Python sandbox:** persistent Python session, bounded execution, and chart capture
- **Java sandbox:** compile and run Java code for supported Workplace tasks
- **Web search:** multi-source querying, deduplication, trust scoring, and synthesis
- **Codebase access:** inspect and patch an explicitly connected workspace, with validation
- **Session memory:** in-session episodic context for Workplace roles and study sessions
- **Persona memory:** persistent user preferences and context stored locally
- **Smart context compaction:** preserves important requirements as conversations grow

User data is stored under `%LOCALAPPDATA%\Axiom`; Visual Studio/debug runs use the separate
`%LOCALAPPDATA%\Axiom-Dev` profile. Axiom does not place chats, API keys, connector tokens,
local models, or Workplace sessions inside release packages.

## Background and system tray

Settings includes separate controls for background operation and the system tray. When both
are enabled, closing the window hides Axiom in the tray instead of ending active work. Axiom
stops UI activity while hidden and releases heavy local model caches after active work finishes.
Use **Exit Axiom** from the tray menu to stop the process completely.

When background operation or tray mode is disabled, closing the main window exits normally.

## Getting started

1. Download the latest Windows ZIP from the [Releases](../../releases) page.
2. Extract the complete folder; do not run the executable from inside the ZIP.
3. Launch `Malx_AI.exe`.
4. Import or install a GGUF model, configure Hybrid Local, or add an OpenRouter API key.
5. Start a Normal Chat or open Workplace.

## Updating

Axiom V1.7.0 and newer checks the official stable GitHub Releases feed at startup. When a
newer release is available, use the in-app update notification or **Settings → General →
Check for updates**. Axiom downloads and verifies the release package, stages it outside the
installation directory, replaces only package-managed files after shutdown, and restarts.
It updates the current portable installation in place rather than leaving an older app
running beside the new version; obsolete managed files, temporary backups, completed
downloads, and staging folders are removed after a successful restart.

Settings, chats, local models, connectors, and Workplace data remain untouched. Manual ZIP
installation remains available for first-time installation and recovery.

The update notification includes a short summary generated from the GitHub Release notes.
Set the `AXIOM_UPDATE_DIR` environment variable to an absolute folder when downloads and
staging should be stored outside `%LOCALAPPDATA%`.

Release maintainers should follow [RELEASING.md](RELEASING.md). Update ZIPs require a matching
version tag, packaged executable version, and `AXIOM_UPDATE_MANIFEST.txt`.

## System requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 (64-bit) |
| RAM | 4 GB minimum; 16 GB recommended for local models |
| CPU | Modern x64 CPU |
| GPU | Optional NVIDIA CUDA acceleration |
| Runtime | Self-contained Windows release; .NET 10 SDK required for development |

## Built with

- C#, WPF, and .NET 10
- LLamaSharp / llama.cpp with CUDA 12 support
- Python.Included
- Markdig and KaTeX
- HtmlAgilityPack
- AvalonEdit
- UglyToad.PdfPig
- SQLite
- WebView2

## Screenshots

**Neuron — Live Neural Map**

A real-time visual map of active sessions, tool usage, and AI activity across Chat,
Workplace, Documents, Study, and Calculator.

<img width="1907" height="995" alt="Neuron tab showing live neural map with connected nodes for Chat, Workplace, Documents, Study, and Calculator" src="https://github.com/user-attachments/assets/99db5dac-67c9-4aac-9415-bb9a30c4f0b7" />

**Workplace Council Mode**

<img width="1886" height="986" alt="Axiom Workplace showing council roles, project canvas, and live activity panel" src="https://github.com/user-attachments/assets/5a58cc02-dc01-440b-a145-b32844e675f8" />

## License

CC BY-NC-ND 4.0 — see [LICENSE](LICENSE) for details.

The source code is publicly viewable, but it may not be redistributed, modified, or used
commercially without explicit permission from the author.

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009)

- [MalxLabs.work](https://malxlabs.work)
- [MalxInference.work](https://malxinference.work/)
- [Axiominference.work](https://axiominference.work/)
