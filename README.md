
# Axiom

A free, local-first AI assistant for Windows built around a multi-model council pipeline.  
Runs entirely on your machine — no subscriptions, no cloud dependency, no data leaving your device.

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Release](https://img.shields.io/badge/release-V1.2-brightgreen)
![.NET](https://img.shields.io/badge/.NET-10-purple)

<img width="1906" height="1026" alt="Axiom screenshot" src="https://github.com/user-attachments/assets/07bbb46d-1bc8-42d7-a16c-5912d2f874d8" />

---

## Table of Contents

- [What Makes Axiom Different](#what-makes-axiom-different)
- [Features](#features)
- [Default Model — Edios 1](#default-model--edios-1)
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
via OpenRouter — giving you free access to Edios 1 (GPTOSS 120B, a 120B reasoning AI model)
and Hepha 1 (Kimi K2's reasoning AI model for coding) with your own API key.
No cost, no subscription, you control the key.

---

## Features

**Normal Chat**
- Local inference using any GGUF model via LLaMA.net
- Optional cloud mode with Edios 1 and Hepha 1 via OpenRouter
- Python, JavaScript, PowerShell, C#, and Java sandbox execution
- Web search with multi-source synthesis
- LaTeX math rendering for equations and formulas
- Thinking mode with forced chain-of-thought reasoning
- Document attachment and analysis
- Artifact Rendering

**Workplace Council Mode**
- Three-role pipeline: Architect plans, Builder implements, Critic reviews
- Static validation and code sandboxing before Critic review
- Confidence routing with targeted patch or full revision
- Session memory (Hippocampus) for persistent context
- Study session for pre-processing documents
- Task history, diff view, and workspace templates
- Qwen3-Coder-480B-A35B-Instruct as the council AI model

---

## Default Model — Edios 1

Edios 1 is Axiom's recommended default model, based on GPT-OSS 120B.
Use it from cloud mode after inserting your API key via **Settings → Cloud AI**.

> Edios 1 is based on OpenAI's GPT OSS 120B, licensed under Apache 2.0.

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
- LLaMA.net (llama.cpp bindings)
- Python.Included
- WebView2
- KaTeX

---

## License

MIT License — see [LICENSE](LICENSE) for details.

---

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009)  
Portfolio: [MalxLabs.work](https://malxlabs.work)
