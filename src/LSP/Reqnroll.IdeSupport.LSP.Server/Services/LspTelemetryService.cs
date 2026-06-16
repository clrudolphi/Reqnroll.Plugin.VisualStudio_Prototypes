#nullable enable

using System.Collections.Generic;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Services;

/// <summary>
/// Sends <c>telemetry/event</c> notifications to the LSP client via
/// <see cref="ILanguageServerFacade.SendNotification"/>.
/// </summary>
public sealed class LspTelemetryService : ILspTelemetryService
{
    private readonly ILanguageServerFacade _languageServer;

    public LspTelemetryService(ILanguageServerFacade languageServer)
    {
        _languageServer = languageServer;
    }

    public void SendEvent(string eventName, Dictionary<string, object?> properties)
    {
        _languageServer.SendNotification(LspMethodNames.TelemetryEvent, new
        {
            eventName,
            properties
        });
    }
}
