import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { ReqnrollMethods } from './lspMethods';
import { openAndReveal } from './navigationUtils';

interface FindStepUsagesResponse {
  isBinding: boolean;
  locations: FindStepUsageItem[];
}

interface FindStepUsageItem {
  uri: string;
  startLine: number;
  startChar: number;
  endLine: number;
  endChar: number;
  stepText?: string;
  keyword?: string;
  scenarioName?: string;
  projectName?: string;
}

interface FindUnusedStepDefinitionsResponse {
  items: UnusedStepDefinitionItem[];
}

interface UnusedStepDefinitionItem {
  projectName?: string;
  className?: string;
  methodName?: string;
  bindingExpression?: string;
  sourceFile?: string;
  sourceLine: number;
  sourceChar: number;
}

export async function doFindStepUsages(
  client: LanguageClient,
  uriStr: string,
  line: number,
  char: number,
): Promise<void> {
  let response: FindStepUsagesResponse | null | undefined;
  try {
    response = await client.sendRequest<FindStepUsagesResponse | null>(
      ReqnrollMethods.findStepUsages,
      {
        textDocument: { uri: uriStr },
        position: { line, character: char },
        context: { includeDeclaration: false },
      },
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Find Step Usages failed — ${msg}`);
    return;
  }

  if (!response?.isBinding) {
    void vscode.window.showInformationMessage(
      'Reqnroll: The cursor is not on a step definition binding.',
    );
    return;
  }

  if (response.locations.length === 0) {
    void vscode.window.showInformationMessage(
      'Reqnroll: No usages found for this step definition.',
    );
    return;
  }

  const items = response.locations.map((loc) => {
    const keyword = loc.keyword ?? '';
    const stepText = loc.stepText ?? '';
    const label = stepText
      ? `$(file-code) ${[keyword, stepText].filter(Boolean).join(' ')}`
      : `$(file-code) ${vscode.Uri.parse(loc.uri).path.split('/').pop() ?? loc.uri}`;
    return { label, description: loc.scenarioName, detail: loc.projectName, loc };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.locations.length} step usage(s) — select to navigate`,
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!picked) return;

  await openAndReveal(vscode.Uri.parse(picked.loc.uri), picked.loc.startLine, picked.loc.startChar);
}

export async function doFindUnusedStepDefinitions(client: LanguageClient): Promise<void> {
  let response: FindUnusedStepDefinitionsResponse;
  try {
    response = await vscode.window.withProgress(
      {
        location: vscode.ProgressLocation.Notification,
        title: 'Reqnroll: Scanning for unused step definitions…',
        cancellable: false,
      },
      () =>
        client.sendRequest<FindUnusedStepDefinitionsResponse>(
          ReqnrollMethods.findUnusedStepDefinitions,
          {},
        ),
    );
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    void vscode.window.showErrorMessage(`Reqnroll: Find Unused Step Definitions failed — ${msg}`);
    return;
  }

  if (!response.items || response.items.length === 0) {
    void vscode.window.showInformationMessage('Reqnroll: No unused step definitions found.');
    return;
  }

  const items = response.items.map((item) => {
    const name = [item.className, item.methodName].filter(Boolean).join('.');
    return {
      label: `$(warning) ${name}`,
      description: item.bindingExpression,
      detail: item.projectName,
      item,
    };
  });

  const picked = await vscode.window.showQuickPick(items, {
    placeHolder: `${response.items.length} unused step definition(s) — select to navigate`,
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!picked?.item.sourceFile) return;

  await openAndReveal(
    vscode.Uri.file(picked.item.sourceFile),
    picked.item.sourceLine,
    picked.item.sourceChar,
  );
}
