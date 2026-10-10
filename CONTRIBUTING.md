# Contributing to Axiom

Thanks for wanting to help. Bug fixes, small features, docs and tests are all welcome.

## How changes get in

1. **Fork** this repository and create a branch for your change.
2. Make the change, run the tests (below), and push the branch to your fork.
3. Open a **pull request** against `main` and fill in the template.
4. The maintainer reviews it. Automated checks build the app and run the tests on Windows.
5. Only the maintainer can merge. A pull request may get change requests, be merged, or be
   closed if it doesn't fit the app's direction. Nothing reaches a release without that review.

For anything bigger than a small fix, please [open an issue](https://github.com/YoMosa2009/Axiom/issues/new/choose)
first so we can agree on the approach before you spend time on it.

## Building and testing

You need Windows 10/11 (64-bit) and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Malx_AI/Malx_AI.csproj -c Debug
dotnet test Malx_AI.Tests/Malx_AI.Tests.csproj
```

A Debug build keeps its data in `%LOCALAPPDATA%\Axiom-Dev`, separate from a normal install.
Only one copy of Axiom can run at a time, so close the installed app before starting yours.

`CLAUDE.md` describes the architecture, where things live, and many non-obvious pitfalls.
It's worth reading before changing the chat pipeline, the Workplace or Agent Access.

## What makes a pull request easy to accept

- **One change per pull request**, with a clear description of what and why.
- **Tests** for logic changes. Pure logic belongs in dependency-free files that the test
  project links (see `Malx_AI.Tests/Malx_AI.Tests.csproj`).
- **Match the surrounding code**: naming, comment style, and how similar features already work.
- **Don't touch releases**: version numbers, `CHANGELOG.md` release entries, `.github/workflows/`
  and `scripts/` are maintained by the maintainer. Pull requests that change them won't be merged.
- **Never include secrets**: API keys, tokens, passwords, or personal data, even in examples.

## Licensing of contributions

Axiom is licensed under [PolyForm Noncommercial 1.0.0](LICENSE). By opening a pull request you
confirm that you wrote the contribution (or have the right to submit it), and you agree that it
is provided under the same license, and that the maintainer may also distribute it under other
terms as part of Axiom.

## Reporting problems

- Bugs and ideas: [GitHub issues](https://github.com/YoMosa2009/Axiom/issues/new/choose), or
  **Settings → General → Send feedback** in the app.
- Security problems: please report privately, see [SECURITY.md](SECURITY.md).
