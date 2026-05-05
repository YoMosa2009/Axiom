
# Axiom

A free, local-first AI assistant for Windows built around a multi-model council pipeline. 
Runs entirely on your machine with no subscriptions, no cloud dependency, and no data 
leaving your device.

<img width="1906" height="1026" alt="image" src="https://github.com/user-attachments/assets/07bbb46d-1bc8-42d7-a16c-5912d2f874d8" />


## What Makes Axiom Different

Most AI tools send your conversations to a server. Axiom runs everything locally using 
GGUF models on your own hardware. Your conversations stay on your machine. Always.

For users who want access to more powerful models, Axiom includes optional cloud mode 
via OpenRouter — giving you free access to Edios 1 ( GPTOSS 120B, a 120B reasoning AI model) and Hepha 1 (Kimi K2's reasoning AI model for coding) with your own 
API key. No cost, no subscription, you control the key.

## Features

**Normal Chat**
- Local inference using any GGUF model via LLaMA.net
- Optional cloud mode with Edios 1 and Hepha 1 via OpenRouter
- Python, JavaScript, PowerShell, C#, and Java sandbox execution
- Web search with multi-source synthesis
- LaTeX math rendering for equations and formulas
- Thinking mode with forced chain-of-thought reasoning
- Document attachment and analysis

**Workplace Council Mode**
- Three-role pipeline: Architect plans, Builder implements, Critic reviews
- Static validation and code sandboxing before Critic review
- Confidence routing with targeted patch or full revision
- Session memory (Hippocampus) for persistent context
- Study session for pre-processing documents
- Task history, diff view, and workspace templates

**Edios 1 — The Default Model**
Axiom ships with  as its recommended default model, based on GPT-OSS 120B
use it from the cloud mode (after inserting your API key).

## System Requirements

- Windows 10 or Windows 11
- 4GB RAM minimum, 16GB recommended
- Any modern CPU (GPU acceleration supported but not required)
- .NET 10 Runtime

## Getting Started

1. Download the latest installer from the [Releases](../../releases) page
2. Run the installer and launch Axiom
3. Import a GGUF model using the Import AI Model button
4. Start chatting

For cloud mode, create a free account at [openrouter.ai](https://openrouter.ai), 
generate an API key, and paste it in Settings under Cloud AI.

## Updating

When a new version is released, download the latest installer from the 
[Releases](../../releases) page and run it. Your settings, chat history, 
and workspace sessions are preserved between updates.

## Default Model — Edios 1

Edios 1 is Axiom's recommended model based on GPT-OSS 120B


## Built With

- C# / WPF / .NET 10
- LLaMA.net (llama.cpp bindings)
- Python.Included
- WebView2
- KaTeX

## License

MIT License — see [LICENSE](LICENSE) for details.

The Edios 1 default model is based on OpenAI's GPT OSS 120B, 
licensed under Apache 2.0.

## Author

Built by [YoMosa2009](https://github.com/YoMosa2009)  
Portfolio: [MalxLabs.work](https://malxlabs.work)
