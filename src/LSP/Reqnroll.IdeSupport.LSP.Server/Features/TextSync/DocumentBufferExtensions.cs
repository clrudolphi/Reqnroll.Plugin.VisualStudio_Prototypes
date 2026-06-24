using Reqnroll.IdeSupport.LSP.Server.Document;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

public static class DocumentBufferExtensions
{
    public static IGherkinTextSnapshot ToGherkinTextSnapshot(this DocumentBuffer buffer)
            => new LspTextSnapshot(buffer.Uri.ToString(), buffer.Version ?? 0, buffer.Text);
}
