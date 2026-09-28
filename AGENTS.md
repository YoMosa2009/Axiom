## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- In this Codex environment, the short `graphify` command may not be on PATH. Use `C:\Users\user\.local\bin\graphify.EXE` directly if needed. Treat Graphify output as an orientation aid: verify important claims with `rg` and direct source reads before making code changes or giving exact answers.

## Shipping fixes and features (cloud sessions)

Merging to `main` with a new `<Version>` publishes an OTA update:
`.github/workflows/release.yml` builds, tests, tags, and releases on Windows, and
installed copies update in-app. This container is Linux, so the WPF app cannot be built
or tested here; the PR's `CI` workflow (Windows) is the build and test gate.

**Until the owner says to ship**, work normally: make the change on the session's branch,
push, open the PR, and fix CI until green (never skip or disable tests). Then report what
changed and that it is ready to ship.

**When the owner says to ship** — "ship it" or any clear equivalent ("release it", "push
the update", "send it out", "go ahead and release") — the owner has authorized merging
and releasing without further confirmation:

1. Bump the version per `VERSIONING.md` (PATCH for fixes, MINOR for features), based on
   the latest release on GitHub, in all four places: `Malx_AI/Malx_AI.csproj`
   `<Version>`, the `AppVersionLabel` in `Malx_AI/MainWindow.xaml`, the `README.md`
   release badge, and a new dated `## [VX.Y.Z] - YYYY-MM-DD` entry at the top of
   `CHANGELOG.md` written for users (it becomes the release notes). Commit as
   `Release VX.Y.Z: <summary>`.
2. Merge the latest `main` into the branch; if that version was released meanwhile,
   bump again. Wait for CI to be green on the final commit.
3. Mark the PR ready for review and merge it (merge commit).
4. Confirm the `Release` workflow run on `main` succeeded and that release `vX.Y.Z` exists
   with `Axiom-vX.Y.Z-win-x64-clean.zip` attached. If it failed, diagnose and ship the
   fix as the next version; never delete or overwrite a published release.
5. Report the version and the release link.

Ambiguous wording is not a ship signal; ask.
