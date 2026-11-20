# Axiom Terms of Service

*Last updated: 2026-11-19*

These Terms govern your use of the optional **Axiom Cloud** paid subscription service inside the Axiom desktop application. Using Axiom locally on your own hardware or with your own third-party API keys (BYOK) remains free under the repository license.

## 1. The Axiom Cloud Subscription
- **Price & Billing Cycle:** Axiom Cloud costs **\$9.00 USD per month**, billed monthly in advance through **Stripe** unless canceled.
- **Included Access:** Active subscribers receive managed access to **DeepSeek 4.1F-1017** in Axiom (Normal Chat, Workplace Agent, Vision, and Computer Use) backed by a **\$5.00 USD monthly upstream inference allowance**.
- **Usage Pacing Limits:** To ensure reliable capacity and help monthly allowances last across the billing period, usage is paced with:
  - A **3-hour rolling session limit** (`\$0.35` allowance per active 3-hour window)
  - A **7-day weekly limit** (`\$1.25` allowance per week)
  - Remaining allowance and reset countdowns are visible anytime inside **Settings → Usage**. Unused allowance does not roll over to the next billing period.

## 2. Cancellation & Billing Portal
- You may cancel your subscription at any time in **Settings → Cloud AI → Manage billing** (which opens Stripe's Billing Portal).
- Cancellation takes effect at the end of your current paid billing period. No partial-month prorated refunds are issued automatically once billing period inference allowance has been provisioned.
- Chargebacks, payment disputes, or fraudulent transactions result in immediate revocation of remaining period allowance and suspension of managed cloud access.

## 3. Acceptable Use & Account Security
- Do not automate bulk account creation, share or resell bearer session tokens outside the Axiom desktop client, attempt to bypass rate limits, or submit illegal or abusive workloads.
- Save your Axiom recovery key in a password manager; recovery keys allow migrating your account and active subscription to a new email address.

## 4. Availability & Changes
- Upstream model inference is routed through third-party infrastructure providers. Occasional provider outages, latency spikes, or upstream rate limits may occur.
- Terms, pricing, and included model configurations may be updated ahead of future billing renewals, with changes reflected in the app and at [github.com/YoMosa2009/Axiom](https://github.com/YoMosa2009/Axiom).

## 5. Support & Disputes
For billing assistance or bug reports, open an issue at [github.com/YoMosa2009/Axiom/issues](https://github.com/YoMosa2009/Axiom/issues) (never post private keys, recovery keys, or payment details publicly) or review your Stripe receipt and Billing Portal.
