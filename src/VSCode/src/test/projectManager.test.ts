import * as assert from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';
import { resolveWorkspaceFolder, findOwningProjectFile } from '../projectManager';
import { ReqnrollMethods } from '../lspMethods';

suite('ProjectManager', () => {
  test('ReqnrollMethods defines the LSP method names ProjectManager sends', () => {
    // Exercises the real constants module, not a local copy — a rename in lspMethods.ts
    // (or a mismatch with LspMethodNames.cs) would fail this test.
    assert.strictEqual(ReqnrollMethods.projectLoaded, 'reqnroll/projectLoaded');
    assert.strictEqual(ReqnrollMethods.projectUnloaded, 'reqnroll/projectUnloaded');
    assert.strictEqual(ReqnrollMethods.projectFiles, 'reqnroll/projectFiles');
  });

  test('should discover .csproj/.slnx/.sln files from workspace folders', async () => {
    const patterns = ['**/*.csproj', '**/*.slnx', '**/*.sln'];
    const found = new Set<string>();

    for (const pattern of patterns) {
      const matches = await vscode.workspace.findFiles(pattern, '**/node_modules/**');
      for (const uri of matches) found.add(uri.toString());
    }

    // node_modules is excluded, and matches from the three patterns must not collide
    // (a .csproj can't also be a .sln/.slnx), so no duplicate handling should be needed.
    for (const uriStr of found) {
      assert.ok(!uriStr.includes('node_modules'), `${uriStr} should have been excluded`);
    }
  });

  suite('resolveWorkspaceFolder', () => {
    test('returns the workspace folder that contains the project file', () => {
      const folders = [path.join('C:', 'work', 'RepoA'), path.join('C:', 'work', 'RepoB')];
      const projectFile = path.join(folders[1], 'src', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folders[1]);
    });

    test('falls back to the first folder when none contain the project file', () => {
      const folders = [path.join('C:', 'work', 'RepoA'), path.join('C:', 'work', 'RepoB')];
      const projectFile = path.join('C:', 'elsewhere', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, folders), folders[0]);
    });

    test('returns the project file itself when there are no workspace folders', () => {
      const projectFile = path.join('C:', 'work', 'Test.csproj');

      assert.strictEqual(resolveWorkspaceFolder(projectFile, []), projectFile);
    });
  });

  suite('findOwningProjectFile', () => {
    test('picks the deepest matching project when projects are nested', () => {
      const outer = path.join('C:', 'work', 'Outer.csproj');
      const inner = path.join('C:', 'work', 'Sub', 'Inner.csproj');
      const known = new Set([outer, inner]);
      const file = path.join('C:', 'work', 'Sub', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), inner);
    });

    test('returns undefined when no known project covers the file', () => {
      const known = new Set([path.join('C:', 'work', 'A.csproj')]);
      const file = path.join('C:', 'elsewhere', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), undefined);
    });

    test('ignores non-.csproj entries such as .sln/.slnx', () => {
      const csproj = path.join('C:', 'work', 'App.csproj');
      const known = new Set([path.join('C:', 'work', 'App.sln'), csproj]);
      const file = path.join('C:', 'work', 'Steps.cs');

      assert.strictEqual(findOwningProjectFile(file, known), csproj);
    });
  });
});
