import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { createTraceChannel } from './lspInspectorLogger';
import { ProjectManager } from './projectManager';
import { StatusBarManager } from './statusBar';
import { doToggleComment } from './commentToggle';
import { doFindStepUsages, doFindUnusedStepDefinitions } from './stepUsages';
import { doGoToHooks } from './hookNavigation';
import { doGoToStepDefinition } from './stepNavigation';
import { registerStepCodeLens } from './stepCodeLens';
import {
  ManualDocumentSync,
  createManualSyncMiddleware,
  isCSharpDocument,
} from './manualDocumentSync';
import { registerTelemetry } from './telemetry';

let client: LanguageClient | undefined;
let projectManager: ProjectManager | undefined;
let statusBar: StatusBarManager | undefined;

/**
 * Resolves the path to the Reqnroll LSP server binary.
 *
 * In development (VSIX not yet built), the server is located relative to
 * this source directory's build output. In production (packaged .vsix),
 * the server is bundled inside the extension under `server/<rid>/`.
 */
function resolveServerPath(context: vscode.ExtensionContext): string {
  const isProduction = context.extensionMode === vscode.ExtensionMode.Production;

  if (isProduction) {
    const rid =
      process.platform === 'win32'
        ? 'win-x64'
        : process.platform === 'darwin'
          ? process.arch === 'arm64'
            ? 'osx-arm64'
            : 'osx-x64'
          : 'linux-x64';
    const serverDir = path.join(context.extensionPath, 'server', rid);
    const binaryName =
      process.platform === 'win32'
        ? 'Reqnroll.IdeSupport.LSP.Server.exe'
        : 'Reqnroll.IdeSupport.LSP.Server';
    const candidate = path.join(serverDir, binaryName);
    if (fs.existsSync(candidate)) {
      return candidate;
    }
    const legacy = path.join(context.extensionPath, 'server', binaryName);
    if (fs.existsSync(legacy)) {
      return legacy;
    }
    throw new Error(
      `Reqnroll LSP server not found at ${candidate} or ${legacy}. ` +
        'Ensure the server is published (see scripts/publish-server.sh).',
    );
  }

  return path.join(
    context.extensionPath,
    '..',
    '..',
    'src',
    'LSP',
    'Reqnroll.IdeSupport.LSP.Server',
    'bin',
    'Release',
    'net10.0',
    'win-x64',
    'publish',
    'Reqnroll.IdeSupport.LSP.Server.exe',
  );
}

export function activate(context: vscode.ExtensionContext): void {
  const notReady = (label: string) => () => {
    void vscode.window.showInformationMessage(
      `Reqnroll: ${label} will be available once the LSP server is ready.`,
    );
  };

  const outputChannel = vscode.window.createOutputChannel('Reqnroll LSP', { log: true });
  const traceChannel = createTraceChannel();

  context.subscriptions.push(
    outputChannel,
    traceChannel,

    vscode.commands.registerCommand('reqnroll.showOutputChannel', () => outputChannel.show()),

    // F13 — Comment/Uncomment (Ctrl+/ for gherkin files)
    vscode.commands.registerCommand('reqnroll.toggleComment', async () => {
      if (!client) {
        notReady('Toggle Comment')();
        return;
      }
      await doToggleComment(client);
    }),

    // F14 — Find Step Usages (invoked from command palette, context menu, or CodeLens click)
    // When invoked from a CodeLens the server passes [uri, line, char] as arguments.
    vscode.commands.registerCommand('reqnroll.findStepUsages', async (...args: unknown[]) => {
      if (!client) {
        notReady('Find Step Usages')();
        return;
      }
      let uriStr: string;
      let line: number;
      let char: number;
      if (args.length >= 2 && typeof args[0] === 'string' && typeof args[1] === 'number') {
        // Called from CodeLens with server-supplied arguments
        uriStr = args[0];
        line = args[1];
        char = typeof args[2] === 'number' ? args[2] : 0;
      } else {
        // Called from command palette or editor context menu
        const editor = vscode.window.activeTextEditor;
        if (!editor) return;
        uriStr = editor.document.uri.toString();
        line = editor.selection.active.line;
        char = editor.selection.active.character;
      }
      await doFindStepUsages(client, uriStr, line, char);
    }),

    // F14 — No-op command for CodeLens items that report 0 usages. Deliberately absent from
    // package.json's contributes.commands: it's only ever invoked as a CodeLens click target
    // (see StepCodeLensHandler.cs), never from the command palette, so it doesn't need a
    // manifest entry — VS Code only requires one for palette/keybinding/menu visibility.
    vscode.commands.registerCommand('reqnroll.noStepUsages', () => {
      void vscode.window.showInformationMessage(
        'Reqnroll: This step definition has no usages in any feature file.',
      );
    }),

    // F15 — Find Unused Step Definitions
    vscode.commands.registerCommand('reqnroll.findUnusedStepDefinitions', async () => {
      if (!client) {
        notReady('Find Unused Step Definitions')();
        return;
      }
      await doFindUnusedStepDefinitions(client);
    }),

    // F17 — Go to Hooks
    vscode.commands.registerCommand('reqnroll.goToHooks', async () => {
      if (!client) {
        notReady('Go to Hooks')();
        return;
      }
      await doGoToHooks(client);
    }),

    // F5 — Go to Step Definition (rich picker with method name + step type)
    vscode.commands.registerCommand('reqnroll.goToStepDefinition', async () => {
      if (!client) {
        notReady('Go to Step Definition')();
        return;
      }
      await doGoToStepDefinition(client);
    }),

    // F6 — Define Steps (delegates to VS Code's native code-action picker)
    vscode.commands.registerCommand('reqnroll.defineSteps', async () => {
      await vscode.commands.executeCommand('editor.action.quickFix');
    }),

    // F16 — Rename Step (delegates to VS Code's native rename; server handles textDocument/rename)
    vscode.commands.registerCommand('reqnroll.renameStep', async () => {
      await vscode.commands.executeCommand('editor.action.rename');
    }),
  );

  // ── Server path resolution ──────────────────────────────────────────────────
  let serverPath: string;
  try {
    serverPath = resolveServerPath(context);
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err);
    void vscode.window
      .showErrorMessage(`Reqnroll: ${message}`, 'Open Documentation')
      .then((choice) => {
        if (choice === 'Open Documentation') {
          void vscode.env.openExternal(
            vscode.Uri.parse(
              'https://github.com/clrudolphi/Reqnroll.Plugin.VisualStudio_Prototypes',
            ),
          );
        }
      });
    return;
  }

  // ── LSP client ─────────────────────────────────────────────────────────────
  const serverOptions: ServerOptions = {
    command: serverPath,
    args: ['--ide', 'vscode'],
    options: {
      env: { ...process.env },
    },
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ language: 'gherkin', pattern: '**/*.feature' }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.{feature,cs}'),
    },
    outputChannel,
    traceOutputChannel: traceChannel,
    // .cs sync is driven manually (see manualDocumentSync.ts) because
    // vscode-languageclient's built-in sync has proven unreliable for it; this middleware
    // stops the built-in path from also emitting sync notifications for .cs documents.
    middleware: createManualSyncMiddleware(isCSharpDocument),
  };

  client = new LanguageClient('reqnroll', 'Reqnroll Language Server', serverOptions, clientOptions);

  statusBar = new StatusBarManager(client);
  context.subscriptions.push(statusBar);

  client
    .start()
    .then(() => {
      projectManager = new ProjectManager(client!);
      // F18 — Step usage CodeLens for C# files (registered after client is running)
      registerStepCodeLens(client!, context);
      // Manually sync .cs documents (see manualDocumentSync.ts / createManualSyncMiddleware
      // above) instead of relying on vscode-languageclient's built-in sync feature.
      context.subscriptions.push(new ManualDocumentSync(client!, isCSharpDocument));
      // Forward server-emitted telemetry/event notifications to Application Insights.
      registerTelemetry(client!, context);
    })
    .catch((err: unknown) => {
      const msg = err instanceof Error ? err.message : String(err);
      void vscode.window.showErrorMessage(`Reqnroll LSP server failed to start: ${msg}`);
    });
}

export function deactivate(): Thenable<void> | undefined {
  projectManager?.dispose();
  return client?.stop();
}
