import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * A VS Code LogOutputChannel that simultaneously writes to the VS Code Output
 * panel and to a timestamped log file in the Reqnroll log directory.
 *
 * File path convention (matches the Visual Studio extension):
 *   Windows : %LOCALAPPDATA%\Reqnroll\reqnroll-vscode-inspector-<ts>.log
 *   macOS   : ~/Library/Logs/Reqnroll/reqnroll-vscode-inspector-<ts>.log
 *   Linux   : ~/.local/share/Reqnroll/reqnroll-vscode-inspector-<ts>.log
 */
class TeeLogOutputChannel implements vscode.LogOutputChannel {
  readonly name: string;
  private readonly _inner: vscode.LogOutputChannel;
  private _stream: fs.WriteStream | undefined;

  constructor(name: string, stream: fs.WriteStream | undefined) {
    this._inner = vscode.window.createOutputChannel(name, { log: true });
    this.name = this._inner.name;
    this._stream = stream;
  }

  get logLevel(): vscode.LogLevel {
    // Always report Trace so vscode-languageclient's LogOutputChannelTracer
    // doesn't gate-keep messages before calling trace()/debug()/info() on us.
    return vscode.LogLevel.Trace;
  }

  get onDidChangeLogLevel(): vscode.Event<vscode.LogLevel> {
    return this._inner.onDidChangeLogLevel;
  }

  trace(message: string, ...args: unknown[]): void {
    this._inner.trace(message, ...args);
    this._file('TRACE', message, args);
  }

  debug(message: string, ...args: unknown[]): void {
    this._inner.debug(message, ...args);
    this._file('DEBUG', message, args);
  }

  info(message: string, ...args: unknown[]): void {
    this._inner.info(message, ...args);
    this._file('INFO', message, args);
  }

  warn(message: string, ...args: unknown[]): void {
    this._inner.warn(message, ...args);
    this._file('WARN', message, args);
  }

  error(message: string | Error, ...args: unknown[]): void {
    this._inner.error(message, ...args);
    const text = message instanceof Error ? message.message : message;
    this._file('ERROR', text, args);
  }

  append(value: string): void {
    this._inner.append(value);
    this._stream?.write(value);
  }

  appendLine(value: string): void {
    this._inner.appendLine(value);
    this._stream?.write(value + '\n');
  }

  replace(value: string): void {
    this._inner.replace(value);
  }

  clear(): void {
    this._inner.clear();
  }

  show(preserveFocus?: boolean): void;
  show(column?: vscode.ViewColumn, preserveFocus?: boolean): void;
  show(_colOrFocus?: vscode.ViewColumn | boolean, _focus?: boolean): void {
    this._inner.show();
  }

  hide(): void {
    this._inner.hide();
  }

  dispose(): void {
    this._inner.dispose();
    this._stream?.end();
    this._stream = undefined;
  }

  private _file(level: string, message: string, args: unknown[]): void {
    if (!this._stream) return;
    const ts = new Date().toISOString();
    const extra = args.length > 0 ? ' ' + args.map((a) => JSON.stringify(a)).join(' ') : '';
    this._stream.write(`${ts} [${level}] ${message}${extra}\n`);
  }
}

/**
 * Creates a trace output channel whose verbosity is controlled by the
 * `reqnroll.trace.server` VS Code setting (`off` | `messages` | `verbose`).
 *
 * When the setting is not `off`, a log file is opened in the Reqnroll log
 * directory so that every JSON-RPC message captured by vscode-languageclient
 * is persisted alongside the VS Code Output panel entry.
 */
export function createTraceChannel(): vscode.LogOutputChannel {
  const level = vscode.workspace.getConfiguration('reqnroll').get<string>('trace.server', 'off');

  if (level === 'off') {
    return vscode.window.createOutputChannel('Reqnroll LSP Trace', { log: true });
  }

  let stream: fs.WriteStream | undefined;
  try {
    const logDir = resolveLogDirectory();
    fs.mkdirSync(logDir, { recursive: true });
    const ts = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    const logPath = path.join(logDir, `reqnroll-vscode-inspector-${ts}.log`);
    stream = fs.createWriteStream(logPath, { flags: 'a' });
  } catch {
    // File logging unavailable; the VS Code output channel is the fallback.
  }

  return new TeeLogOutputChannel('Reqnroll LSP Trace', stream);
}

function resolveLogDirectory(): string {
  switch (process.platform) {
    case 'win32':
      return path.join(process.env['LOCALAPPDATA'] ?? os.homedir(), 'Reqnroll');
    case 'darwin':
      return path.join(os.homedir(), 'Library', 'Logs', 'Reqnroll');
    default:
      return path.join(os.homedir(), '.local', 'share', 'Reqnroll');
  }
}
