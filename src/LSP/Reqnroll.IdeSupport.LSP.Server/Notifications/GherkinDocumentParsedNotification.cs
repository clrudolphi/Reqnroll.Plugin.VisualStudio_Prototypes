using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Editor.Services.Parsing.GherkinDocuments;

namespace Reqnroll.IdeSupport.LSP.Server.Notifications;

public record GherkinDocumentParsedNotification(
    DocumentUri Uri,
    int Version,
    IReadOnlyCollection<DeveroomTag> Tags) : INotification;
