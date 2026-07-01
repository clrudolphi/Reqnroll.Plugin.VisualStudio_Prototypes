# Contributing to the Reqnroll VS Code Extension

## Prerequisites

- [Node.js](https://nodejs.org/) 22 or later
- [.NET SDK](https://dotnet.microsoft.com/download) 10.0 or later (for server publish)
- [VS Code](https://code.visualstudio.com/) 1.96 or later
- Recommended VS Code extensions: ESLint (`dbaeumer.vscode-eslint`), Prettier (`esbenp.prettier-vscode`)

## Repository layout

```
src/VSCode/               ← this directory (TypeScript extension)
  src/
    extension.ts          ← entry point (activate / deactivate)
    projectManager.ts     ← reqnroll/projectLoaded notifications
    msbuildEvaluator.ts   ← dotnet msbuild property evaluation
    lspInspectorLogger.ts ← optional JSON-RPC file logger
    statusBar.ts          ← LSP server status bar item
  syntaxes/               ← TextMate grammar (.tmLanguage.json)
  scripts/
    publish-server.sh     ← publishes the LSP server for all RIDs
    build-vsix.sh         ← packages the .vsix
    validate-semantic-token-scopes.mjs  ← CI validation
  tests/VSCode/           ← standalone Mocha test project (grammar + unit tests)
src/LSP/                  ← the shared LSP server (C#)
```

## Activation events

`package.json`'s `activationEvents` includes `workspaceContains:**/*.feature` in addition to
the more obvious `onLanguage:gherkin`. This is intentional, not left over: F18's step-usage
CodeLens (`stepCodeLens.ts`) needs the LSP client and `ProjectManager` running as soon as a
`.cs` file with step-definition CodeLenses is opened — which can happen before the user ever
opens a `.feature` file (`onLanguage:gherkin` wouldn't have fired yet). `workspaceContains` lets
the extension activate as soon as the workspace is known to be a Reqnroll project, independent
of which file the user opens first. Don't remove it without checking that CodeLens still shows
up on a `.cs` file opened before any `.feature` file.

## Development workflow

### 1. Build the LSP server

The extension bundles the LSP server binary. For local development, publish it once:

```sh
cd src/VSCode
npm run build:server
```

This runs `scripts/publish-server.sh`, which publishes the server for your host platform into `src/VSCode/server/<rid>/`.

### 2. Install npm dependencies

```sh
cd src/VSCode
npm ci
```

### 3. Open in VS Code

Open the `src/VSCode` folder in VS Code. Press **F5** to launch the Extension Development Host with the extension loaded. A new VS Code window will open with the extension active.

### 4. Live TypeScript compilation

In a terminal:

```sh
cd src/VSCode
npm run watch
```

This keeps `out/` up to date as you edit `.ts` files, so the Extension Development Host picks up changes on reload (`Ctrl+R` in the dev host window).

### 5. Running tests

```sh
cd tests/VSCode
npm ci && npm test
```

Tests cover the TextMate grammar and utility functions. They run without VS Code.

### 6. Lint and format

```sh
cd src/VSCode
npm run lint
npm run format          # write (auto-fix)
npm run format:check    # check only (used in CI)
```

### 7. Packaging

```sh
cd src/VSCode
npm run build:vsix
```

This publishes the server for all four RIDs and packages the `.vsix` in one step. Requires Docker or cross-compilation support for non-host RIDs.

## LSP tracing

To see raw JSON-RPC traffic, open VS Code Settings and set:

```
reqnroll.trace.server: verbose
```

Traffic appears in the **Output** panel under **Reqnroll LSP Trace**. When set to `verbose`, a timestamped log file is also written to `%LOCALAPPDATA%\Reqnroll\` (Windows) or `~/.local/share/Reqnroll/` (macOS/Linux).

## CI

The GitHub Actions workflow [`.github/workflows/build-vscode-extension.yml`](../../.github/workflows/build-vscode-extension.yml) runs on every push to `feat/vscode-extension-**` branches. It:

1. Publishes the server for all four RIDs in parallel
2. Compiles TypeScript, lints, format-checks, and validates semantic token scopes
3. Packages the `.vsix`
