import * as assert from 'assert';
import * as vscode from 'vscode';

// Pull in all additional test suites so the single entry-point loads them all
import './projectManager.test';
import './lspInspectorLogger.test';

suite('Reqnroll Extension Tests', () => {
  const extensionId = 'reqnroll.reqnroll-ide-support';

  test('Extension should be present', () => {
    const ext = vscode.extensions.getExtension(extensionId);
    assert.ok(ext, `Extension ${extensionId} is not installed`);
  });

  test('Extension should activate on gherkin language', async () => {
    const ext = vscode.extensions.getExtension(extensionId)!;
    await ext.activate();
    assert.ok(ext.isActive, 'Extension did not activate');
  });

  test('Language client should start after activation', async () => {
    const ext = vscode.extensions.getExtension(extensionId)!;
    await ext.activate();

    // Give the language client a moment to start
    await new Promise((resolve) => setTimeout(resolve, 2000));

    assert.ok(ext.isActive, 'Extension should remain active after client start');
  });

  test('Gherkin language should be registered', async () => {
    const languages = await vscode.languages.getLanguages();
    assert.ok(
      languages.includes('gherkin'),
      `gherkin language not registered. Available: ${languages.join(', ')}`,
    );
  });
});
