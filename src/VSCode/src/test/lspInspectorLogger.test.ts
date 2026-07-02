import * as assert from 'assert';
import * as vscode from 'vscode';
import { traceServerToLogLevel } from '../lspInspectorLogger';

suite('traceServerToLogLevel', () => {
  const config = vscode.workspace.getConfiguration('reqnroll');

  teardown(async () => {
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
  });

  test('defaults to Warning when the setting is unset', async () => {
    await config.update('trace.server', undefined, vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Warning');
  });

  test("maps 'off' to Warning", async () => {
    await config.update('trace.server', 'off', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Warning');
  });

  test("maps 'messages' to Info", async () => {
    await config.update('trace.server', 'messages', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Info');
  });

  test("maps 'verbose' to Verbose", async () => {
    await config.update('trace.server', 'verbose', vscode.ConfigurationTarget.Global);
    assert.strictEqual(traceServerToLogLevel(), 'Verbose');
  });
});
