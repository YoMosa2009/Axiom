# OpenRouter usage and app user-flow checks — 2026-11-17

## Usage correctness

`GET /api/v1/key` reports USD credit spending separately from
`data.free_model_daily_requests`. The previous code treated a null USD spending
limit as unlimited requests, and could also interpret credit amounts as counts.

The parser now reads only the named free-model request counter. It uses the
reported remaining count (or derives it from the named used/limit fields if
remaining is absent). Missing or invalid counters display an unavailable state,
not an invented cap or unlimited requests.

The pill shows and fills the remaining percentage. For example, 38 of 50 requests
remaining is `76% left`. The fill uses `PrimaryAccentBrush` dynamically, including
theme changes without reopening the page. Usage refreshes when stale settings
are opened, while Cloud settings are visible, and after normal cloud responses.
Cache freshness expires after 30 seconds and at UTC midnight. Refreshes have a
20-second deadline; clearing/replacing a key cancels and invalidates earlier work.

Primary references:
- https://openrouter.ai/docs/api_reference/limits
- https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key

A single read-only live request using the existing Dev-profile key verified the
new parser: 0 used / 50 limit / 50 remaining at the time of the check. The saved
database was opened read-only; the credential stayed in memory and was not
printed or exported. No completion requests were made for this check.

## Confirmed defects corrected during delegated testing

- Settings pages shared an old scroll offset and their navigation scrolled away.
  The title/category navigation is now fixed above the scrollable page contents,
  and page selection/reopening resets the offset.
- Compact Workplace auto-collapse left preference and actual visibility out of
  sync. Context/Diff/Canvas now open on the first click and highlight the shown tab.
- Main navigation ignored clicks during its two fades. It now applies the latest
  requested tab after the current transition rather than dropping it.
- Fixed-name atomic staging overwrote an unrelated `<target>.tmp` sibling.
  Saves now own unique staging files and clean them after commit or failure.
- Cancellation could update a `.bak` without committing the new primary. A short
  commit lock and cancellation checks preserve the last committed state; legacy
  `.tmp` recovery remains supported and consumes the recovered snapshot.
- Debug startup imported legacy data into custom data directories. Shared OAuth
  hydration/lookup also crossed this boundary. Custom profiles now stay isolated;
  default Debug migrations and default OAuth sharing remain available. Explicit
  custom-root, environment and built-in OAuth configuration continue to resolve.

The isolation defects were discovered when test preflight found imported data.
Further probes were paused until both startup paths were corrected. Temporary
imported profile copies were removed without deleting the source user profiles.
Earlier Settings probes may have issued key/catalog and endpoint metadata reads,
but did not invoke Chat Send. All final UI probes verified clean profiles before
testing; no model-generation requests were made.

## Validation

- Final WPF Debug build: zero errors, 352 existing warnings.
- Full xUnit suite: 929 passed, zero failures/skips, including 23 new quota cases
  and six persistence regressions.
- Usage renderer: 19 checks passed across both themes, two widths, remaining
  percentages, unknown/error states, and cancellation/key-clear reset.
- GPT 6.2 Sol Settings/Chat agent: 119 WPF UIA interactions, 58 checks passed at
  measured 1520×900 and 1040×640 DIPs; included 40 rapid settings-page changes,
  both themes, empty key/malformed endpoint, menus, draft and empty canvas.
- GPT 6.2 Sol Workplace agent: 112 UIA actions, ten scenarios, 79/80 assertions
  passed. Covered Council/single setup, review, Skills, web/scope controls,
  guarded no-model sends, compact/wide sidebars and four rapid tab sequences.
  The remaining assertion used an invalid native file-picker target; exact
  selection/removal/cancellation is unverified, not a reproduced product bug.
  Selected files were staged build artifacts, not user data or credentials.
- GPT 6.2 Sol safety agent: 92 focused tests passed. A before/after linked-source
  harness reproduced the old staging/cancellation bugs and verified 25 concurrent
  save pairs plus recovery. Four fresh-process Debug/Release migration scenarios
  passed using fake legacy data; the old Debug/custom case failed as expected.
- OAuth isolation: seven linked-production-source fixture checks passed, using
  synthetic credentials only.

Evidence lives in ignored local artifacts: `openrouter-usage-acceptance/`,
`usage-stress-tests.log`, `usage-stress-build-final.log`,
`settings-stress/run-acceptance/`, `settings-stress/oauth-fixture/result.log`,
and `workplace-stress/final/report.json`.

This was a bounded UI/local-feature test pass. Live generation, rich populated
canvas content, real account OAuth sign-in, and the native attachment picker flow
were not accepted end to end. An earlier restore-time CollectionView exception
occurred in the harness before its dispatcher started and was not reproduced in
normal startup, so no speculative product change was made for it.

Initially committed locally as `cf58509`, with no version bump or OTA release.
On 2026-11-17 the owner authorized GitHub source/docs synchronization. See
[ROADMAP.md](../ROADMAP.md) for the publication scope and current source status.
