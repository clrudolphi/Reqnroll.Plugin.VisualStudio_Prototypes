using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Thin convenience wrappers that send raw LSP messages by method name, so the specs do not
/// depend on which strongly-typed extension methods a given OmniSharp version exposes.
/// </summary>
public static class LspClientExtensions
{
    public static void OpenDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Version = version,
                LanguageId = "gherkin",
                Text = text
            }
        });

    public static void ChangeDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didChange", new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = text })
        });

    /// <summary>Opens a C# document (languageId <c>csharp</c>), used to drive Roslyn binding discovery.</summary>
    public static void OpenCSharpDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.SendNotification("textDocument/didOpen", new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Version = version,
                LanguageId = "csharp",
                Text = text
            }
        });

    /// <summary>Changes a C# document with the full updated text (full-sync), driving re-discovery.</summary>
    public static void ChangeCSharpDocument(this ILanguageClient client, DocumentUri uri, int version, string text)
        => client.ChangeDocument(uri, version, text);

    public static void CloseDocument(this ILanguageClient client, DocumentUri uri)
        => client.SendNotification("textDocument/didClose", new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri }
        });

    public static Task<SemanticTokens?> RequestSemanticTokensAsync(
        this ILanguageClient client, DocumentUri uri, CancellationToken ct = default)
        => client.SendRequest("textDocument/semanticTokens/full",
                new SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } })
            .Returning<SemanticTokens?>(ct);

    /// <summary>
    /// Requests semantic tokens, retrying briefly until a non-empty result is available
    /// (the server parses asynchronously after didOpen/didChange).
    /// </summary>
    public static async Task<SemanticTokens?> RequestSemanticTokensWhenReadyAsync(
        this ILanguageClient client, DocumentUri uri, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        SemanticTokens? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await client.RequestSemanticTokensAsync(uri).ConfigureAwait(false);
            if (last is { Data.Length: > 0 }) return last;
            await Task.Delay(50).ConfigureAwait(false);
        }
        return last;
    }

    public static void SendProjectLoaded(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectLoaded", payload);

    public static void SendProjectUnloaded(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectUnloaded", payload);

    public static void SendProjectFiles(this ILanguageClient client, object payload)
        => client.SendNotification("reqnroll/projectFiles", payload);

    public static Task<LocationOrLocationLinks?> RequestReferencesAsync(
        this ILanguageClient client, DocumentUri uri, int line, int character, CancellationToken ct = default)
        => client.SendRequest("textDocument/references",
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = uri },
                    Position     = new Position(line, character),
                    Context      = new ReferenceContext { IncludeDeclaration = false }
                })
            .Returning<LocationOrLocationLinks?>(ct);
}
