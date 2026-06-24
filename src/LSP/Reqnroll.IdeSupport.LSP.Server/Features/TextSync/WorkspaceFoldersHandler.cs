using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.TextSync;

/// <summary>
/// Handles <c>workspace/didChangeWorkspaceFolders</c> notifications.
/// Opens/closes <see cref="LspProjectScope"/> instances in response to the client
/// adding or removing workspace roots.
/// </summary>
public class WorkspaceFoldersHandler : IDidChangeWorkspaceFoldersHandler
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IDeveroomLogger _logger;

    public WorkspaceFoldersHandler(ILspWorkspaceScopeManager scopeManager, IDeveroomLogger logger)
    {
        _scopeManager = scopeManager;
        _logger = logger;
    }

    public Task<Unit> Handle(DidChangeWorkspaceFoldersParams request, CancellationToken cancellationToken)
    {
        if (request.Event?.Added is not null)
        {
            foreach (var folder in request.Event.Added)
            {
                var path = folder.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInfo($"Workspace folder added: {path}");
                    _scopeManager.OpenWorkspace(path);
                }
            }
        }

        if (request.Event?.Removed is not null)
        {
            foreach (var folder in request.Event.Removed)
            {
                var path = folder.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInfo($"Workspace folder removed: {path}");
                    _scopeManager.CloseWorkspace(path);
                }
            }
        }

        return Unit.Task;
    }

    public DidChangeWorkspaceFolderRegistrationOptions GetRegistrationOptions(ClientCapabilities clientCapabilities)
        => new() { ChangeNotifications = true };
}
