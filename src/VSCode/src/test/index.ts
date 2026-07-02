import * as path from 'path';
import Mocha from 'mocha';

/**
 * Bootstraps the Mocha `suite`/`test` globals inside the Extension Development Host and runs the
 * bundled test file. `@vscode/test-electron` loads this module's `run` export as
 * `extensionTestsPath` — pointing that directly at a compiled test file (as this used to) never
 * registers Mocha's BDD globals, so every suite/test call throws `ReferenceError: suite is not
 * defined` before a single test runs.
 */
export function run(): Promise<void> {
  // Default Mocha timeout (2000ms) races against extension.test.ts's own 2000ms activation
  // wait; give suites headroom now that they actually run (this harness was previously never
  // invoking them at all — see remarks above).
  const mocha = new Mocha({ ui: 'tdd', color: true, timeout: 10000 });
  const testsRoot = path.resolve(__dirname);

  // extension.test.js pulls in the other suites (projectManager.test, lspInspectorLogger.test)
  // via its own imports, so registering just the entry point is enough.
  mocha.addFile(path.resolve(testsRoot, 'extension.test.js'));

  return new Promise((resolve, reject) => {
    try {
      mocha.run((failures) => {
        if (failures > 0) {
          reject(new Error(`${failures} test(s) failed.`));
        } else {
          resolve();
        }
      });
    } catch (err) {
      reject(err instanceof Error ? err : new Error(String(err)));
    }
  });
}
