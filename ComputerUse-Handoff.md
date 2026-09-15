# Computer Use: observed problems and test evidence

Work on the existing Computer Use system in Axiom. This handoff is limited to problems observed during testing; it does not prescribe a solution.

## Scope and expectation

The problem is not limited to browser automation or to one repository/navigation scenario. Computer Use needs to reliably understand a user's stages, goals, actions, failures, and progress for general desktop tasks and long-horizon, multi-step work. It must not be tailored to a specific user, GitHub account, repository, browser, or example prompt.

## General interaction reliability

- It often fails to click controls precisely or accurately after it has opened an application.
- It can miss visible targets, repeat a missed click, or become stuck trying to recover from a missed target.
- It can claim that an action was successful even when the visible screen does not show the claimed application, action, or result.
- It sometimes appears to hallucinate progress: logs claimed Microsoft Edge had opened and was visible while the actual screen remained the desktop.
- Post-action screenshots and the execution ledger can report an observed visible change even when the expected state is absent.
- The agent can keep producing Computer Use output without performing further visible actions for several minutes.

## Long-horizon and multi-goal task failures

A repeated repro was a request with two independent outcomes: open a specified destination in the first browser tab, then open a YouTube video about a requested topic in a different tab.

- It may complete the first goal, then return to the completed destination instead of advancing to the next goal.
- It has repeatedly retyped the first destination after the first goal was already complete.
- It has typed, deleted, and retyped text in the address bar while appearing stuck.
- It can create unnecessary third tabs while failing to complete the requested second-tab task.
- Completed goals are not reliably retained as completed when the agent plans later steps.
- It may attempt the second destination in the original tab even when the request explicitly requires a different tab.
- It can treat an unresolved tab requirement as permission to continue with same-tab navigation.

## New-tab and focus failures

- `Ctrl+T` frequently fails to produce a result the controller recognizes, even when a new tab is visibly open.
- In some runs, `Ctrl+T` appears to have no visible effect; in other runs, a new tab is visible but the controller still believes only one tab exists.
- The fallback click on the visible `+` New tab button has sometimes opened the tab successfully, but later navigation in that new tab still stalls.
- The logs have included: `[TAB VERIFICATION FAILED] Ctrl+T did not produce an observed additional tab. Keep this goal unfinished; ensure the browser has focus before trying a different recovery.`
- Other logs then claimed that a previous Ctrl+T caused a visible change while also stating that the browser still showed only one tab.
- The controller has blocked its own progress after tab creation, including messages such as `Blocked a navigation URL that differs from controller evidence.`
- The agent has focused the address bar rather than resolving the requested new-tab state, then remained stuck behind its own restriction.

## Navigation and text-entry failures

- A specified destination has sometimes been entered correctly, and sometimes been entered as the wrong path despite the same style of request.
- The wrong destination has led to a visible GitHub 404 / “This is not the web page you are looking for” page.
- A full URL and a search phrase have been concatenated into malformed address-bar text, for example a YouTube URL immediately followed by the requested video topic.
- The agent can type a URL into a focused address bar and then stop without pressing Enter or otherwise committing navigation.
- It can open a new tab, focus its address bar, announce that it will type the destination, then remain idle while only logs continue to update.
- The agent sometimes reports a destination as verified even when the current browser page is an error page.

## State verification failures

- The system does not consistently use fresh screen evidence to determine whether an action actually worked.
- It has marked a browser open/visible while the screenshot showed only the desktop.
- It has marked a destination complete when the browser showed a 404 page.
- It has described the expected state in logs as if it were the actual state, for example: `Inspect this fresh screenshot for the expected state: Microsoft Edge window is open and visible`, while the expected state was not visible.
- It can claim that a URL is entered or a browser control is focused, but then fail to make the next action or confirm the requested end state.

## Action parsing and recovery failures

- One run produced a raw code fence beginning with ````json` in the Computer Use log and then: `Could not parse an action. Asking the model to retry.`
- The system can get into repeated recovery behavior rather than making meaningful forward progress.
- The log has reported `Blocked a repeated click on the same missed target.` while the task remained unfinished.
- The system can alternate between narrating an intended action, recording a screenshot, and re-evaluating the same state without carrying out the required next action.
