# Screenshot follow-up after owner testing — 2026-11-16

## Latest chat and log evidence

Read the newest Workplace chat in the development profile's
`ChatHistory/chat_advanced_state.json`, updated at 20:36, plus backend events/errors.
The 20:24 task built the LLM inference visualizer in 13 steps, versus the earlier 20/44-step
follow-ups. The summary distinguishes initial rendering from unverified interactive playback,
although its opening sentence overstates the result as rendering correctly.

The new timing events confirm that this was a build containing the preceding fixes:
file writes took 7–8 ms; successful screenshots took 1.680 and 0.922 seconds; the slowest
executed command took 7.627 seconds. The longest model decision took 46.748 seconds.
There were also long gaps between model decisions and command execution. Manual approval
mode was enabled, so waiting for approval is a plausible explanation for those gaps; the
logs do not independently establish every gap's cause.

The visible step list still includes a WScript.Shell command and later native window/process
commands between screenshots. Full arguments were not persisted, so the logs alone do not
prove which command minimized or covered Axiom. Confirmed implementation gaps were:

- The first WScript/AppActivate shell call was allowed; only subsequent calls were blocked.
- Native window-control APIs were absent from that detector.
- Opening a visible browser to verify a page can cover Axiom even without minimizing it.
- Captured images were passed to the model but were not attached to the user's live run card.

## Changes

- Window-control workarounds are blocked from their first call at both the session and tool
  execution boundaries, including background commands. This covers WScript/AppActivate,
  ShowWindow/ShowWindowAsync, SetForegroundWindow, SetWindowPos, MinimizeAll and related APIs.
  It is a conservative execution guard, not a complete sandbox for arbitrary scripts. Explicit
  Computer Use input remains separate. User intent is still interpreted by the model.
- `take_screenshot` accepts `url` for generated HTML pages. Edge/Chrome/Brave renders the URL
  in headless mode with a unique temporary profile, no visible browser and a 25-second limit.
  Local file targets must exist and belong to the selected project. Network-share and
  non-HTTP/file schemes are rejected. Only the owned capture process/profile is cleaned up.
- Successful screenshots appear in compact "Viewed an image" dropdowns with 80-pixel
  thumbnails during live thinking/activity, matching the owner's supplied reference. They
  use a separate transient property from user image attachments. Completion, cancellation
  and splitting a run card clear them; snapshots and final responses do not include them.
  A late queued UI update cannot reintroduce a preview after completion.
  Default captures are stored under the app profile's `WorkplaceCaptures` directory.
- Screenshot instructions prefer the background path for page verification and explicitly
  distinguish an initial render from proof of playback or interaction.

The headless flags follow the official [Chrome command-line reference](https://developer.chrome.com/docs/automation-and-testing/headless-cli).

## Validation

- Full regression suite: 892 passed, zero failed/skipped. Includes first-call blocking,
  executor/background bypass checks, no approval for blocked window control, scoped page URL
  validation, and screenshot image-path reporting.
- Debug app build: zero errors, existing warnings.
- Production background-capture probe rendered a 1280 × 900 local page in 8.696 seconds.
  The image contains the expected background and the JavaScript-updated text. Visibility,
  minimized state and foreground focus were unchanged across six desktop windows.
- Actual compiled Workplace view on a private desktop with an isolated profile: screenshot
  thumbnail fits the 80-pixel dropdown, expands/collapses, is absent from saved snapshots,
  and is removed by the actual run-completion method before the final response is shown.
  The host remained visible in Normal state. The rendered dropdown was visually inspected.
  Evidence: `artifacts/workplace-preview-check4/report.json` and `workplace-preview.png`.
- No model requests or real-profile UI writes were used for validation. Native probe artifacts
  are under `artifacts/pagecheck`. An initial screenshot is not an interactive playback test.

Changes were initially local and remain unreleased. The owner authorized GitHub
source/docs synchronization on 2026-11-17; see [ROADMAP.md](../ROADMAP.md) for the
publication scope and current source status.
