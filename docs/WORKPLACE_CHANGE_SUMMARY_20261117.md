# Completed Workplace change summary — 2026-11-17

The owner's reference shows a rounded edit-summary card after the answer: document
badge, file count, added/deleted line totals, Undo and View changes actions, three
file rows, then Show more files. Workplace now uses this layout after the completed
answer and collapsed activity list when recorded file or Canvas changes exist.

## Behavior

- Header and file rows use the selected theme's surfaces, borders, text and semantic
  green/red colors. Buttons have rounded hover/pressed/focus treatment. At narrow
  widths, actions move onto another row and long paths trim with a full-path tooltip.
- Three files show initially. Show more/fewer expands or restores the preview.
  Individual rows open their captured diff; View changes reviews the complete list.
  The review window selects a file and offers selectable, scrollable before/after
  diff text, computed off the UI thread.
- File-tool edits use the Agent run journal's actual before-copy, even if the model
  never called read_file. Applied codebase patches use their pre-apply undo snapshot
  and observed resulting file. Canvas uses the run's before/after source, explicitly
  labeled as a Canvas update rather than a filesystem edit.
- Repeated changes to a path collapse to the net before/after change. Unchanged and
  reverted writes are omitted. Empty created/deleted files remain real changes.
  Binary, unreadable, invalid UTF-8 or oversized files have unknown counts instead
  of fabricated additions/deletions. Text capture is limited to 512 KB per file.
- Summary metadata and captured text persist with each chat card. Review shows the
  captured version rather than silently reading the current disk state on click.
  A restored chat retains review history without reviving an unsafe Undo action.
- Undo is relocated from the composer bar to its completed Agent response. Only the
  latest eligible in-memory Agent journal exposes it. It uses existing guarded Undo:
  later-edited files are skipped, and the run's versions are moved aside. Clearing
  the journal or undoing it removes the action. Existing undo notices still update
  conversation context and persist.
- Existing activity details, answer text and full tool evidence remain unchanged.

## Scope and limits

The card represents observed file-tool and applied patch edits, plus Canvas source
updates. It does not infer file edits from prose or claim to count arbitrary shell
commands, external desktop edits or background jobs. A scope tooltip explains this;
unreadable rows explicitly say Not counted. Text-only replies have no empty or
invented file-edit panel. Codebase patches retain their existing separate Undo
interface; this card exposes guarded Agent-journal Undo only.

## Validation

- Full portable suite: 954 passed, zero failed/skipped. New cases cover the actual
  journal baseline without read_file, unchanged/failed writes, empty creations and
  deletions, binary/oversized/missing originals, net patches, missing-baseline
  preservation, serialized captured diffs and Canvas classification.
- WPF Debug build: zero errors and 352 existing warnings. The isolated UI fixture
  passed 19 checks covering
  expansion, counts, captured review windows, history restoration, narrow layouts,
  alternate themes, text-only replies, applied patches, Canvas completion and a real
  owned-file Undo through the footer button.
- The UI fixture uses a fresh verified custom profile and a private Windows desktop.
  It sends no live model requests and uses UI Automation to invoke controls; it does
  not send mouse/keyboard input to the user's desktop or edit their project files.

Ignored local evidence: `artifacts/workplace-changes-tests.log`,
`artifacts/workplace-changes-final-build.log`,
`artifacts/workplace-changes-final-acceptance/`, and
`artifacts/workplace-changes-final-graphify.log`.

Committed locally only. No push, version bump or release.
