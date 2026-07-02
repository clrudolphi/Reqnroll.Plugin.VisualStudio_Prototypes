# Reqnroll.IdeSupport

LSP-based IDE support for [Reqnroll](https://reqnroll.net/), the open-source Gherkin/BDD test
framework for .NET. One shared LSP server drives Gherkin syntax highlighting, diagnostics,
navigation, completions, formatting, and refactoring across multiple IDEs, replacing the legacy
monolithic [`Reqnroll.VisualStudio`](https://github.com/reqnroll/Reqnroll.Visualstudio) VS
extension with a design that works the same way in Visual Studio, VS Code, and (planned) Rider.

> **Status:** Preview / active development. The Visual Studio and VS Code extensions are
> functional and cover most of the planned feature set; see
> [Open Questions & Risk Register](docs/LSP-IDE-Support-Open-Questions.md) for what's still
> unresolved and the [Issues](../../issues) tab for tracked defects and to-dos. Not yet promoted
> as the recommended replacement for the legacy extension — see
> [Overview §5](docs/LSP-IDE-Support-Overview.md#5-release-strategy-and-migration-plan) for the
> promotion criteria.

## Why this exists

The legacy `Reqnroll.VisualStudio` extension is a single VS-SDK codebase with no path to VS Code
or Rider. This project extracts the Gherkin-editing intelligence (parsing, diagnostics, step
matching, navigation, formatting) into a standalone [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
server, so each IDE only needs a thin client. See
[docs/LSP-IDE-Support-Overview.md](docs/LSP-IDE-Support-Overview.md) for the full goals,
non-goals, and phased roadmap.

## Repository layout

```
src/
  Core/
    Reqnroll.IdeSupport.Common                    ← shared config/logging/analytics contracts
    Reqnroll.IdeSupport.ReqnrollConnector.Models   ← DTOs shared with the out-of-proc connector
  LSP/
    Reqnroll.IdeSupport.LSP.Core                   ← Gherkin parser, binding registry, match cache (netstandard2.0)
    Reqnroll.IdeSupport.LSP.Server                 ← the LSP server itself (OmniSharp.Extensions.LanguageServer host)
    Reqnroll.IdeSupport.LSP.Connector              ← out-of-process reflection-based binding discovery, per-TFM
  VisualStudio/
    Reqnroll.IdeSupport.VisualStudio.Extension     ← VS.Extensibility LSP client (VSIX)
    Reqnroll.IdeSupport.VisualStudio.VSSDKIntegration ← MEF classifications, analytics, VSSDK fallback
    Reqnroll.IdeSupport.VisualStudio.Wizards*      ← New Project / New Item wizards, welcome dialog
    Reqnroll.IdeSupport.VisualStudio.ItemTemplates,
    Reqnroll.IdeSupport.VisualStudio.ProjectTemplate ← VSIX template packaging
  VSCode/                                          ← TypeScript VS Code extension (npm project)

tests/            ← unit tests, integration specs, and BDD spec projects mirroring src/
docs/             ← design docs (see below)
```

Each subproject's own README/CONTRIBUTING/XML doc comments explain its internals in more detail;
the [Architecture reference](docs/LSP-IDE-Support-Architecture.md) is the canonical map of how
everything fits together.

## Design documentation

| Document | Read it for… |
|---|---|
| [Overview](docs/LSP-IDE-Support-Overview.md) | Scope, goals/non-goals, high-level architecture diagram, phased roadmap, release/migration strategy |
| [Architecture & Implementation Reference](docs/LSP-IDE-Support-Architecture.md) | Module design, server internals (workspace model, membership index, pipeline), per-IDE client details, cross-cutting concerns |
| [Feature Designs](docs/LSP-IDE-Support-Feature-Designs.md) | Per-feature (F1–F20) design, sequence diagrams, as-built notes |
| [Open Questions & Risk Register](docs/LSP-IDE-Support-Open-Questions.md) | Active open questions and risks — check here before assuming something is decided |

`docs/Archive/` holds superseded or fully-implemented design/plan documents kept for historical
reference — each doc's own status banner says whether it's active or archived-and-why.

## Building

There's no single top-level build for every project in this repo (the VS extension is
net481/VSSDK, the LSP server is net10.0 cross-platform, and the VS Code extension is a separate
npm project) — build the piece you're working on:

**LSP Server** (net10.0, cross-platform):
```sh
dotnet build src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj
```

**Visual Studio Extension** (net481, requires Visual Studio + the Extensibility/VSSDK workloads):
```sh
dotnet build src/VisualStudio/Reqnroll.IdeSupport.VisualStudio.Extension/Reqnroll.IdeSupport.VisualStudio.Extension.csproj
```
Building the Extension project also publishes the LSP server self-contained (win-x64) into the
VSIX under `LSPServer/` — rebuild the Extension after any server change to pick it up.

**VS Code Extension** (TypeScript):
```sh
cd src/VSCode
npm ci
npm run build:server   # publishes the LSP server for your host platform
npm run compile
```

The .NET projects can also be opened as one workspace via the solution file
[`Reqnroll.IdeSupport.slnx`](Reqnroll.IdeSupport.slnx) in an IDE that supports the `.slnx` format
(Visual Studio 2022 17.13+, VS Code with the C# Dev Kit).

See [CONTRIBUTING.md](CONTRIBUTING.md) and the area-specific contributor guides
([LSP Server](src/LSP/CONTRIBUTING.md), [Visual Studio extension](src/VisualStudio/CONTRIBUTING.md),
[VS Code extension](src/VSCode/CONTRIBUTING.md)) for full development workflows, debugging, and
test instructions.

## Testing

```sh
# LSP server unit tests
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj

# LSP server integration specs (Reqnroll .feature BDD, server hosted in-process)
dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Reqnroll.IdeSupport.LSP.Server.Specs.csproj

# VS extension client-side unit tests
dotnet test tests/VisualStudio/Reqnroll.VisualStudio.Tests/Reqnroll.VisualStudio.Tests.csproj

# VS Code extension tests (grammar + utility functions, no VS Code required)
cd tests/VSCode && npm ci && npm test
```

CI (`.github/workflows/build-vscode-extension.yml`) builds and packages the VS Code extension and
publishes the LSP server for all supported runtimes (win-x64, linux-x64, osx-x64, osx-arm64) on
every push to `feat/vscode-extension-**`/`main` and on pull requests touching `src/LSP/` or
`src/VSCode/`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[BSD 3-Clause](LICENSE.txt).
