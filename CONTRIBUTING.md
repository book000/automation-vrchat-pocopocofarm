# Contributing

Thank you for your interest. Bug reports, fixes, and documentation improvements are welcome.

## Before you start

- This is an unofficial tool for a VRChat world. Contributions must not add functionality that reads or modifies game memory or network traffic; the tool works only from the screen and VRChat's OSC input.
- Open an issue first for larger changes so the approach can be agreed on.

## Setup

You need Windows 11 and the .NET 10 SDK. See [docs/development.md](docs/development.md) for the layout, build and test commands, and conventions.

```powershell
git clone https://github.com/book000/automation-vrchat-pocopocofarm.git
cd automation-vrchat-pocopocofarm
dotnet build -c Release
dotnet test -c Release
```

## Workflow

1. Create a branch from the default branch. Use short, conventional names such as `feat/<topic>`, `fix/<topic>`, `docs/<topic>`, or `chore/<topic>`.
2. Make focused changes. Keep behavior changes and refactoring in separate commits.
3. Add or update tests for logic changes. Detection and classification changes should be backed by a fixture image in `tests/fixtures` when possible (crop screenshots to the relevant area and do not include personal information such as other players' names).
4. Run `dotnet build -c Release` and `dotnet test -c Release` and make sure both pass without new warnings.
5. Update the documentation under `docs/` and the settings reference when behavior or settings change.
6. Use [Conventional Commits](https://www.conventionalcommits.org/) for commit messages (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `ci:`, `chore:`).
7. Open a pull request describing the problem, the change, and how you verified it. CI (build, tests, publish check) must pass.

## Code style

- Follow `.editorconfig`. Nullable reference types are enabled.
- Thresholds and intervals are settings (`src/Pocofarm/Core/Config*.cs`) with a `[Desc]` description, not hard-coded numbers.
- Source comments in this repository are written in Japanese; documentation and error messages are English.
- Do not commit secrets, personal data, `config.json`, or anything under `data/`.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
