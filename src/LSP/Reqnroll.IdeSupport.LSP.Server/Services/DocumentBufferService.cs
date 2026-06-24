using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Gherkin.Parsing;

namespace Reqnroll.IdeSupport.LSP.Server.Services;

public record DocumentBuffer(DocumentUri Uri, int? Version, string Text, IReadOnlyCollection<DeveroomTag>? Tags = null);

public interface IDocumentBufferService
{
    void Update(DocumentUri uri, int? version, string text);
    void UpdateTags(DocumentUri uri, IReadOnlyCollection<DeveroomTag> tags);
    void Remove(DocumentUri uri);
    bool TryGet(DocumentUri uri, out DocumentBuffer? buffer);
    IEnumerable<DocumentBuffer> All { get; }
}

public class DocumentBufferService : IDocumentBufferService
{
    private readonly ConcurrentDictionary<string, DocumentBuffer> _buffers =
        new(StringComparer.OrdinalIgnoreCase);

    public void Update(DocumentUri uri, int? version, string text)
        => _buffers[uri.ToString()] = new DocumentBuffer(uri, version, text);

    public void UpdateTags(DocumentUri uri, IReadOnlyCollection<DeveroomTag> tags)
        => _buffers.AddOrUpdate(
            uri.ToString(),
            _ => new DocumentBuffer(uri, null, string.Empty, tags),
            (_, existing) => existing with { Tags = tags });

    public bool TryGet(DocumentUri uri, out DocumentBuffer? buffer)
        => _buffers.TryGetValue(uri.ToString(), out buffer);

    public void Remove(DocumentUri uri)
        => _buffers.TryRemove(uri.ToString(), out _);

    public IEnumerable<DocumentBuffer> All => _buffers.Values;
}