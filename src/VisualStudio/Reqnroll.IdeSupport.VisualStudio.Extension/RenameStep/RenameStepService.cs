#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RenameStep;

/// <summary>
/// Sends custom <c>reqnroll/renameTargets</c> and <c>reqnroll/selectRenameTarget</c>
/// requests over the <c>LspInterceptingPipe</c> for the F16 Step Rename feature.
/// </summary>
internal sealed class RenameStepService
{
    private const string RenameTargetsMethod = "reqnroll/renameTargets";
    private const string SelectRenameTargetMethod = "reqnroll/selectRenameTarget";
    private const string RenameMethod = "textDocument/rename";

    private readonly LspInterception.LspInterceptingPipe _pipe;
    private readonly TraceSource _traceSource;
    private readonly IDeveroomLogger _fileLogger = new SynchronousFileLogger();

    public RenameStepService(LspInterception.LspInterceptingPipe pipe, TraceSource traceSource)
    {
        _pipe = pipe;
        _traceSource = traceSource;
    }

    /// <summary>
    /// Queries the server for renameable binding targets at the given position.
    /// Returns a <see cref="RenameTargetsResult"/> with the available targets,
    /// or null if no renameable binding was found at the cursor position.
    /// </summary>
    public async Task<RenameTargetsResult?> GetRenameTargetsAsync(
        string fileUri, int line0, int char0,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildPositionParams(fileUri, line0, char0);

        _traceSource.TraceInformation(
            "RenameStepService: querying {0} at {1}:{2}:{3}", RenameTargetsMethod, fileUri, line0, char0);

        var result = await _pipe
            .SendRequestToServerAsync(RenameTargetsMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        if (result is JObject obj)
        {
            try
            {
                var targets = obj["targets"] as JArray;
                if (targets is null || targets.Count == 0)
                {
                    _traceSource.TraceInformation("RenameStepService: no targets returned");
                    return null;
                }

                var response = new RenameTargetsResult();
                foreach (var t in targets)
                {
                    if (t is not JObject item) continue;
                    response.Targets.Add(new RenameTargetItem
                    {
                        Label = item["label"]?.Value<string>() ?? "",
                        Expression = item["expression"]?.Value<string>() ?? "",
                        AttributeIndex = item["attributeIndex"]?.Value<int>() ?? 0
                    });
                }

                _traceSource.TraceInformation(
                    "RenameStepService: {0} target(s) returned", response.Targets.Count);
                return response;
            }
            catch (System.Exception ex)
            {
                _traceSource.TraceEvent(TraceEventType.Warning, 0,
                    "RenameStepService: failed to parse targets: {0}", ex.Message);
            }
        }

        _traceSource.TraceInformation("RenameStepService: no targets returned");
        return null;
    }

    /// <summary>
    /// Tells the server to remember the selected attribute index for the next rename.
    /// </summary>
    public async Task SelectRenameTargetAsync(
        string fileUri, int version, int attributeIndex,
        CancellationToken cancellationToken)
    {
        var paramsJson = $"{{\"uri\":{JsonEscape(fileUri)},\"version\":{version},\"attributeIndex\":{attributeIndex}}}";
        _traceSource.TraceInformation(
            "RenameStepService: sending {0} for attrIndex={1}", SelectRenameTargetMethod, attributeIndex);

        await _pipe
            .SendNotificationToServerAsync(SelectRenameTargetMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the standard <c>textDocument/rename</c> request and returns a structured
    /// <see cref="RenameWorkspaceEdit"/> with pre-parsed file edits, or null if the
    /// server had no edit to return.
    /// </summary>
    public async Task<RenameWorkspaceEdit?> SendRenameRequestAsync(
        string fileUri, int line0, int char0, string newName,
        CancellationToken cancellationToken)
    {
        var paramsJson = BuildRenameParams(fileUri, line0, char0, newName);
        _traceSource.TraceInformation(
            "RenameStepService: sending {0} at {1}:{2}:{3}", RenameMethod, fileUri, line0, char0);
        _fileLogger.LogInfo($"RenameStepService: sending {RenameMethod} at {fileUri}:{line0}:{char0}");

        var result = await _pipe
            .SendRequestToServerAsync(RenameMethod, paramsJson, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
            return null;

        _fileLogger.LogInfo($"RenameStepService: {RenameMethod} returned workspace edit");

        return ParseWorkspaceEdit(result);
    }

    /// <summary>
    /// Parses a raw <c>textDocument/rename</c> JSON result into a <see cref="RenameWorkspaceEdit"/>
    /// with local file paths and sorted text edits.
    /// </summary>
    internal static RenameWorkspaceEdit? ParseWorkspaceEdit(JToken result)
    {
        if (result is not JObject editObj)
            return null;

        var changes = editObj["changes"] as JObject;
        if (changes is null || changes.Count == 0)
            return null;

        var workspace = new RenameWorkspaceEdit();

        foreach (var fileEntry in changes)
        {
            var uri = fileEntry.Key;
            var edits = fileEntry.Value as JArray;
            if (edits is null || edits.Count == 0)
                continue;

            var localPath = UriToLocalPath(uri);
            var parsed = ParseTextEdits(edits);
            if (parsed.Count > 0)
                workspace.FileEdits[localPath] = parsed;
        }

        return workspace.FileEdits.Count > 0 ? workspace : null;
    }

    private static string UriToLocalPath(string uri)
    {
        if (uri.StartsWith("file:///", System.StringComparison.OrdinalIgnoreCase))
            return uri.Substring(8).Replace('/', '\\');
        return uri;
    }

    private static List<TextEditItem> ParseTextEdits(JArray edits)
    {
        var result = new List<TextEditItem>(edits.Count);
        foreach (var edit in edits.Cast<JObject>())
        {
            var range = edit["range"];
            if (range is null) continue;
            var start = range["start"];
            var end   = range["end"];
            if (start is null || end is null) continue;

            result.Add(new TextEditItem(
                start["line"]?.Value<int>() ?? 0,
                start["character"]?.Value<int>() ?? 0,
                end["line"]?.Value<int>() ?? 0,
                end["character"]?.Value<int>() ?? 0,
                edit["newText"]?.Value<string>() ?? ""
            ));
        }

        // Sort descending so edits applied bottom-to-top keep positions valid.
        result.Sort((a, b) =>
        {
            var lineCmp = b.StartLine.CompareTo(a.StartLine);
            return lineCmp != 0 ? lineCmp : b.StartChar.CompareTo(a.StartChar);
        });

        return result;
    }

    /// <summary>
    /// Sends a <c>textDocument/didChange</c> notification so the server re-parses a file
    /// that was modified by the rename workspace edit while closed.
    /// </summary>
    public Task SendDidChangeAsync(string localPath, string newContent, CancellationToken cancellationToken)
    {
        var escapedContent = Newtonsoft.Json.JsonConvert.ToString(newContent);
        var featureUri = "file:///" + localPath.Replace('\\', '/');
        var paramsJson = $"{{\"textDocument\":{{\"uri\":\"{featureUri}\",\"version\":1}},\"contentChanges\":[{{\"text\":{escapedContent}}}]}}";

        _traceSource.TraceInformation(
            "RenameStepService: sending textDocument/didChange for '{0}'", localPath);

        return _pipe.SendNotificationToServerAsync("textDocument/didChange", paramsJson, cancellationToken);
    }

    private static string BuildPositionParams(string fileUri, int line0, int char0)
    {
        var escapedUri = JsonEscape(fileUri);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}}}}";
    }

    private static string BuildRenameParams(string fileUri, int line0, int char0, string newName)
    {
        var escapedUri = JsonEscape(fileUri);
        var escapedNewName = JsonEscape(newName);
        return $"{{\"textDocument\":{{\"uri\":{escapedUri}}},\"position\":{{\"line\":{line0},\"character\":{char0}}},\"newName\":{escapedNewName}}}";
    }

    internal static string JsonEscape(string value) =>
        Newtonsoft.Json.JsonConvert.ToString(value);
}
