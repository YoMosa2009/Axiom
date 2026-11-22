# Axiom Privacy Policy

*Last updated: 2026-11-22*

Axiom is built local-first. Your conversations, files, settings, and third-party API keys stay on your Windows PC unless you explicitly send a prompt to a cloud provider or connect an integration.

## 1. Local Desktop Data
- **Stored only on your PC** (`%LOCALAPPDATA%\Axiom`, or `Axiom-Dev` in debug builds): chat history, Workplace sessions, Persona memory, DPAPI-encrypted BYOK API keys, connector OAuth tokens, and preferences.
- **Persona notes:** saved only on your PC. When **Let Normal Chat use these notes** is on (the default) and you chat with a Cloud or Hybrid Local model, the notes are sent with your message like the rest of the conversation. Turn it off in Persona to keep them local.
- Axiom includes no advertising SDKs or third-party behavioral analytics trackers.

## 2. Optional Axiom Cloud Subscription Data
If you sign in to **Axiom Cloud** in Settings → Cloud AI:
- **Account & Sign-In:** We store your email address, salted hashes of email verification codes, session tokens (expired automatically after 30 days), and any generated recovery key hash so you can sign in and restore your subscription.
- **Payments:** Checkout and card details are processed directly by **Stripe** (`checkout.stripe.com` and `billing.stripe.com`). Axiom never sees or stores your full card number; we store only the Stripe customer identifier, active subscription period timestamps, billing status, and aggregated micro-USD usage totals per request/window.
- **Prompts & Attachments in Cloud Mode:** Prompts, attached images, and enabled tool payloads sent through Axiom Cloud are forwarded to the configured upstream inference provider (DigitalOcean via OpenRouter or configured upstream endpoint) solely to generate your response and settle exact request cost. **Prompts and responses are not stored in the subscription account database.**
- **Backups:** Account and ledger databases are backed up periodically for disaster recovery with temporary email codes, session tokens, and checkout locks scrubbed from snapshots.

## 3. Bring-Your-Own-Key (OpenRouter), Hybrid Local, and Connectors
- **OpenRouter / Custom Endpoints:** Requests go directly from your PC to the endpoint you configured under that provider's own privacy policy.
- **Connectors (Google / GitHub / Todoist):** Only activated when you click **Connect** in Settings → Connectors and grant consent. Tokens are stored encrypted on your machine via Windows DPAPI and only shared with tools you invoke.

## 4. Data Removal & Contact
- Remove local data anytime via **Settings → General → Reset all local app data**.
- Sign out or manage subscription billing anytime in **Settings → Cloud AI**.
- Questions or privacy requests can be submitted via [github.com/YoMosa2009/Axiom](https://github.com/YoMosa2009/Axiom) or [github.com/YoMosa2009/Axiom/security](https://github.com/YoMosa2009/Axiom/security).
