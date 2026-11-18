# Workplace run reliability and presentation — 2026-11-17

## Evidence and reliability changes

The owner confirmed the latest generated WPF app eventually worked when tested.
The two newest conversation turns were reviewed read-only alongside backend and
per-chat tool records. The repair finished with 26 of 60 available steps; most
model decisions took about two seconds, while two commands reached 60-second
timeouts even after their output reported a nonzero GUI window handle. The final
reply also distinguished a stale original executable from a newer SDK output.
The investigation targets avoidable harness work, not a claim that the owner's
working result failed.

A matching isolated regression reproduced the command-runner problem: .NET's
`WaitForExitAsync` also waits for redirected stream EOF, and a launched child can
retain those handles after PowerShell exits. The runner now observes PowerShell
exit independently and allows one second for stream draining. It reports the
actual shell exit code and explicitly warns if later child output is not included.
True deadlines/cancellation still stop the command. Received stdout and raw stderr
are archived on exit, timeout and cancellation; only model-facing stderr is cleaned.

Repair guidance now asks the model to verify the original delivered file/entry
point and the actual build destination, updating that delivery within scope rather
than assuming the SDK's default output path replaced the user's original file.
No user-generated photo-editor files were changed by this work.

Transport history replay was repeatedly appending the same immutable tool result.
Mirroring now records an identified message object once per evidence scope.
Distinct calls, changed copies, reused IDs in distinct messages, and every actual
execution/raw-output record remain available. Existing archives are not rewritten.

## Presentation changes

- Send uses a shared working-state spinner. Idle, cancellation and error cleanup
  restore the arrow; Agent Access still accepts steering while its spinner runs.
  Cleanup ownership prevents a steering message from ending the active run.
- Completed `Steps Executed` lists become a collapsed activity dropdown, including
  older saved cards. The raw content is retained in snapshots/context/copy data.
  Expansion exposes every step in a bounded, scrollable panel. Fenced examples,
  user messages and following answer paragraphs are protected from the legacy
  section parser.
- The final dropdown is a custom rounded theme-accent badge with a rotating
  chevron, hover/pressed feedback and keyboard focus treatment, replacing the
  default white Windows expander.
- Step updates commit before their callback returns, preventing queued live-card
  updates from overwriting the final answer or omitting the last activity entries.
- Existing background toast settings now cover Agent Access, Computer Use, normal
  Council/single-model runs and reruns. User cancellation does not emit completion.
  Toasts request Windows' default notification sound; OS notification/sound settings
  remain authoritative. Showing a toast does not restore or focus Axiom.
- Canvas has a useful empty state, actual artifact title, accessible Preview/Source,
  Copy/Save and patch controls, wrapping actions, and separate session/revision diff
  states. Native previews still suppress correctly under Settings overlays.
- A Canvas-only terminal text colorizer adjusts low-contrast syntax on dark themes,
  preserving tokenization/font styles/global syntax definitions. Tested keyword
  contrast rose from roughly 1.7–2.9:1 to 4.6:1 in the supplied themes. Light canvas
  backgrounds retain the default syntax palette.

## Validation

- WPF Debug build: zero errors; 352 existing warnings.
- Full portable test suite: 943 passed, zero failed/skipped. Includes descendant
  pipe exit 0/7, real PowerShell raw CLIXML retention, evidence replay and protected
  activity-section parsing. The pipe fixture permits cold shell startup while its
  child holds streams open much longer than the test command deadline.
- Canvas agent: 24 private-desktop checks passed at 360 and 290 DIPs, including
  empty/source, actual named local HTML DOM, preview/source toggles, both diff
  views, theme/syntax changes, control bounds and measured syntax contrast.
- Progress/activity probe: 18 checks passed for collapsed/restored activity, complete 26-step
  history, actual finalization ordering, Send animation with and without steering,
  cleanup, mode switching and minimized/hidden notification eligibility. Final
  custom dropdown also checks theme binding and expansion after a theme change.

All UI fixtures use fresh verified custom profiles on a private desktop, with no
real desktop input or live model-generation requests. No completion toast or sound
was posted to the user's desktop during tests. Native toast delivery/audio remains
an OS-level integration check; eligibility and SDK audio configuration were checked.
WPF bitmap captures exclude HWND WebView content, so the real preview was checked
through its native DOM rather than claiming a bitmap verified it.

Local ignored evidence:
- `artifacts/workplace-progress-final-tests.log`
- `artifacts/workplace-progress-dropdown-build.log`
- `artifacts/workplace-progress-polished/`
- `artifacts/canvas-presentation-probe/acceptance/`

Initially committed locally as `83b49cc`. On 2026-11-17 the owner authorized
GitHub source/docs synchronization, without a version bump or release. See
[ROADMAP.md](../ROADMAP.md) for the publication scope and current source status.
