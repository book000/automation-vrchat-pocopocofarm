# Development

## Prerequisites

- Windows 11 (the project targets `net10.0-windows10.0.22621.0` and uses Windows APIs, Windows OCR and Windows.Graphics.Capture, so it cannot be built or tested on other platforms).
- The .NET 10 SDK (see `global.json`; newer feature bands are accepted).
- Git. No other tools are required.

## Repository layout

```
.
├── .editorconfig               Formatting rules
├── .gitattributes              Line endings and binary files
├── .github/
│   └── workflows/              CI (ci.yml), actionlint, reviewer assignment
├── CONTRIBUTING.md
├── Directory.Build.props       Target framework, nullable, implicit usings for all projects
├── LICENSE
├── NOTICE.md                   Third-party notices
├── Pocofarm.slnx               Solution
├── README.md
├── docs/                       Documentation
├── global.json                 SDK version
├── renovate.json               Dependency update configuration
├── scripts/
│   ├── setup_firewall.ps1      Firewall rule for the dashboard
│   └── start.bat               Starts the published executable minimized
├── src/Pocofarm/               The application
│   ├── Cli/  Core/  Farm/  Fishing/  Ocr/  Platform/  Vision/  Web/
│   ├── Models/                 Embedded OCR models and UI icon templates
│   ├── Web/Static/index.html   Dashboard page, embedded
│   ├── app.manifest
│   └── Pocofarm.csproj
├── tests/
│   ├── Pocofarm.Tests/         xUnit tests
│   └── fixtures/               Sample images (farm photos, menu, OCR toasts)
└── artifacts/                  Build output (git-ignored)
```

`config.json` and `data/` are created at runtime under the directory passed as `--root` and are git-ignored.

## Build, test, publish

All commands run from the repository root.

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/Pocofarm -c Release -r win-x64 --self-contained -o artifacts/publish
```

The published `artifacts/publish/pocofarm.exe` is a single self-contained executable of roughly 90 MB. The ONNX Runtime native libraries are extracted next to it on first start. The OCR models and the dashboard page are embedded.

Run it from a scratch directory so that runtime files do not land in the repository:

```powershell
mkdir $env:TEMP\pocofarm-run
& "$PWD\artifacts\publish\pocofarm.exe" doctor --root $env:TEMP\pocofarm-run
```

`--dry-run` runs the whole loop without sending clicks, which is useful to compare behavior without affecting a game session.

### Tests

The tests use xUnit v3. They cover detection and dip logic, toast classification, configuration validation, auto-tuning, the farm grid analysis, and OCR accuracy on the fixture images. Image-based tests find their data by walking up from the test binary to the `tests/fixtures` directory, so keep that directory in the repository. The Windows OCR test needs the Japanese OCR language pack and is skipped on machines without it (the GitHub-hosted Windows runner has none, so CI skips it; the PP-OCR tests still run there).

### Continuous integration

`.github/workflows/ci.yml` runs on `windows-latest`: restore, build in Release, test, and a self-contained `win-x64` publish as a smoke check. Pushes to the default branch also upload the executable as a workflow artifact. `actionlint.yml` and `add-reviewer.yml` come from the shared [book000/templates](https://github.com/book000/templates), and `renovate.json` extends the shared Renovate preset. Third-party actions are pinned to full commit SHAs with a version comment; Renovate maintains them.

## Conventions

- **Language.** Identifiers are English. Source comments and log messages are written in Japanese, as in the existing code; user-facing documentation is English. Error messages that surface as exceptions to developers are English.
- **Nullable reference types** are enabled everywhere; keep the build free of new warnings.
- **Formatting** follows `.editorconfig` (UTF-8, CRLF, four spaces for C#, two for JSON/XML/YAML/HTML).
- **Settings.** Every threshold or interval is a `Config` property, not a constant buried in a method.
- **Responsibilities.** `Fisher` holds state transitions only. Recording, OCR, tuning, and external control belong to the collaborator classes in `Fishing/`. Pure logic that does not need the screen lives in `Core/` and `Vision/` so it can be unit-tested.
- **Never lose an attempt.** Every reel ends in exactly one `attempt` event with an `outcome`; do not add code paths that drop or double-count one.
- **Commits** use [Conventional Commits](https://www.conventionalcommits.org/).

## Adding a setting

1. Add a property to the `Config` partial class of the matching area, with a `[Desc]` attribute and a default value:

   ```csharp
   // src/Pocofarm/Core/Config.Fishing.cs
   [Desc("Seconds to wait before something happens")] public double MyDelay { get; set; } = 1.5;
   ```

   Pass `restart: true` as the second argument of `[Desc]` if a change only takes effect after the worker restarts. The JSON key is derived automatically in snake_case (`my_delay`).
2. Read it where it is needed through the `cfg` instance. For detector thresholds, extend the parameter record (for example `BobberParams`) and its `From(Config)` factory rather than passing `Config` into the detector.
3. Validation is generic: numbers must be finite and non-negative, arrays must have the default's length, and unknown keys are rejected. Nothing else to register; the dashboard's settings card and `GET /api/config` pick the property up through reflection.
4. Add a row to the matching table in [configuration.md](configuration.md).
5. Add or adjust a test if the setting changes logic.

## Adding an outcome or event type

New outcomes must be added to `EventLog.Outcomes` and `EventLog.OutcomeJa`, handled in the statistics, and documented in [architecture.md](architecture.md#outcome-types). Other event types need only a `events.Write("type", ...)` call and an entry in the architecture document's list.
