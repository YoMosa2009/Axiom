
# Axiom

A free, local-first AI assistant for Windows built around a multi-model council pipeline.  
Runs entirely on your machine — no subscriptions, no cloud dependency, no data leaving your device.

![License](https://img.shields.io/badge/license-CC%20BY--NC--ND%204.0-lightgrey)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Release](https://img.shields.io/badge/release-V1.4-brightgreen)
![.NET](https://img.shields.io/badge/.NET-10-purple)

<img width="1906" height="1026" alt="Axiom screenshot" src="https://github.com/user-attachments/assets/07bbb46d-1bc8-42d7-a16c-5912d2f874d8" />

---

## Table of Contents

- [What Makes Axiom Different](#what-makes-axiom-different)
- [Features](#features)
- [AI Pipeline & Tools](#ai-pipeline--tools)
- [Screenshots](#screenshots)
- [Models](#models)
- [System Requirements](#system-requirements)
- [Getting Started](#getting-started)
- [Updating](#updating)
- [Built With](#built-with)
- [License](#license)
- [Author](#author)

---

## What Makes Axiom Different

Most AI tools send your conversations to a server. Axiom runs everything locally using
GGUF models on your own hardware. Your conversations stay on your machine. Always.

For users who want access to more powerful models, Axiom includes optional cloud mode
via OpenRouter — giving you free access to Eidos 1 and Hepha 1 with your own API key.
No cost, no subscription, you control the key.

---

## Features

**Normal Chat**
- Local inference using any GGUF model via LLamaSharp (llama.cpp bindings)
- Optional cloud mode with Eidos 1 and Hepha 1 via OpenRouter
- Python and Java sandbox execution
- Web search with multi-source synthesis and source confidence ranking
- LaTeX math rendering for equations and formulas
- Thinking mode with forced chain-of-thought reasoning
- Document attachment and analysis across 50+ text/code formats, plus PDF, DOCX, XLSX, and images
- Artifact rendering for charts, SVG, HTML, and interactive JavaScript output

**Workplace Council Mode**
- Three-role pipeline: Architect plans, Builder implements, Critic reviews
- Static validation and code sandboxing before Critic review
- Confidence routing with targeted patch or full revision
- Session memory (Hippocampus) for persistent context
- Study session for pre-processing documents
- Task history, diff view, and workspace templates
- Qwen3 Coder 480B as the default council cloud model

---

## AI Pipeline & Tools

A look at what actually runs behind the chat window.

**Tool Agents**
- **Calculator** — evaluates scientific expressions (trig, log, sqrt, and more) and converts units across length, mass, volume, and temperature
- **Python Sandbox** — runs Python 3 in-process via Python.Included, with a persistent session so variables carry across code blocks, a 10-second execution timeout, and chart capture (matplotlib/plotly) rendered back as images
- **Java Sandbox** — compiles and runs Java code submitted by the Workplace Council
- **Web Search** — queries DuckDuckGo, Bing, and Google News, deduplicates results, and scores sources by trust, boosting reference/documentation sites and downranking low-signal hosts

**Workplace Council Pipeline**
- The Architect, Builder, and Critic roles run as a local agentic loop with a budget of up to 3 tool calls per turn (calculator, sandbox, web search, or session-memory lookup) before falling back to plain generation
- The Critic returns a structured contract — status, issues, severity, evidence, and suggested fix — which determines whether the Builder receives a targeted patch or a full revision
- If a tool call fails mid-generation, the pipeline rolls back to the last good model state before retrying

**Memory & Context**
- **Session Hippocampus** — an in-session episodic memory (up to 160 entries) that stores definitions, summaries, and error/solution patterns from the Architect, Builder, Critic, and Study session, surfaced later by keyword relevance and recency
- **Persona Memory** — a persistent, cross-session store of user preferences and context, saved locally and retrieved by relevance to the current query
- **Smart Context Compaction** — detects when a conversation is approaching the model's context limit and compresses lower-priority messages while preserving pinned messages, requirements, and code

---

## Screenshots

**Neuron — Live Neural Map**  
A real-time visual map of active sessions, tool usage, and AI activity across Chat, Workplace, Documents, Study, and Calculator.

<img width="1907" height="995" alt="Neuron tab showing live neural map with connected nodes for Chat, Workplace, Documents, Study, and Calculator" src="https://github.com/user-attachments/assets/99db5dac-67c9-4aac-9415-bb9a30c4f0b7" />

**Workplace Council Mode**  
The three-role council pipeline — Architect, Builder, and Critic — with the Project Canvas, Live Activity feed, and per-role context controls.

<img width="1886" height="986" alt="Axiom Workplace showing council roles, project canvas, and live activity panel" src="https://github.com/user-attachments/assets/5a58cc02-dc01-440b-a145-b32844e675f8" />

---

## Models

Axiom ships with **Axiom Qwen3-4B** (a quantized GGUF model) as its default local model. Any GGUF model can be imported and used in its place.

Cloud mode (via OpenRouter, using your own API key) adds two model aliases plus the Workplace Council's default cloud model. Each is backed by a primary model with automatic fallbacks if a provider is rate-limited or unavailable:

| Alias | Primary Model | Notes |
|---|---|---|
| **Eidos 1** | Gemma 4 26B | General-purpose reasoning |
| **Hepha 1** | Nemotron 3 Super 120B | Code-specialized |
| **Workplace Council (default)** | Qwen3 Coder 480B | Used by the Architect/Builder/Critic pipeline |

Use cloud mode from **Settings → Cloud AI** after inserting your OpenRouter API key.

---

## System Requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 |
| RAM | 4 GB minimum, 16 GB recommended |
| CPU | Any modern CPU (GPU acceleration supported but not required) |
| Runtime | .NET 10 |

---

## Getting Started

1. Download the latest installer from the [Releases](../../releases) page
2. Run the installer and launch Axiom
3. Import a GGUF model using the **Import AI Model** button
4. Start chatting

For cloud mode, create a free account at [openrouter.ai](https://openrouter.ai),
generate an API key, and paste it in **Settings → Cloud AI**.

---

## Updating

Download the latest installer from the [Releases](../../releases) page and run it.
Your settings, chat history, and workspace sessions are preserved between updates.

---

## Built With

- C# / WPF / .NET 10
- LLamaSharp (llama.cpp bindings) with CUDA 12 GPU backend
- Python.Included
- Markdig (Markdown rendering)
- HtmlAgilityPack (web search parsing)
- AvalonEdit (code editor)
- UglyToad.PdfPig (PDF extraction)
- SQLite (local persistence)
- WebView2
- KaTeX

---

## License

CC BY-NC-ND 4.0 — see [LICENSE](LICENSE) for details.

The source code is publicly viewable, but it may not be redistributed, modified, or used commercially without explicit permission from the author.

---

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009)  
Portfolio: [MalxLabs.work](https://malxlabs.work)
