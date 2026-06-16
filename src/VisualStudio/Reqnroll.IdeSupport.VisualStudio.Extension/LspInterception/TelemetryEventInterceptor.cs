using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Analytics;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Intercepts <c>telemetry/event</c> notifications from the LSP server (Receive direction)
/// and forwards them to <see cref="IAnalyticsTransmitter"/> for persistent telemetry.
/// </summary>
/// <remarks>
/// Uses a lazy <c>Func&lt;IAnalyticsTransmitter?&gt;</c> (same pattern as
/// <see cref="ScaffoldTrackingInterceptor"/>) so the transmitter can be resolved
/// after the MEF composition is ready, rather than requiring it at construction time.
/// </remarks>
internal sealed class TelemetryEventInterceptor : ILspMessageInterceptor
{
    // Local constant rather than LspMethodNames.TelemetryEvent because the VS Extension
    // references LSP.Server with <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    // and therefore cannot consume its types at compile time.
    private const string TelemetryEventMethod = "telemetry/event";

    private readonly Func<IAnalyticsTransmitter?> _getTransmitter;
    private readonly TraceSource _trace;

    public TelemetryEventInterceptor(
        Func<IAnalyticsTransmitter?> getTransmitter,
        TraceSource trace)
    {
        _getTransmitter = getTransmitter ?? throw new ArgumentNullException(nameof(getTransmitter));
        _trace          = trace          ?? throw new ArgumentNullException(nameof(trace));
    }

    public Task<LspInterceptorResult> InterceptAsync(
        LspMessage        message,
        CancellationToken cancellationToken)
    {
        // Only interested in notifications from the server (Receive direction).
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        if (message.Method != TelemetryEventMethod)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var transmitter = _getTransmitter();
        if (transmitter is null)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "TelemetryEventInterceptor: IAnalyticsTransmitter not available; dropping event.");
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }

        try
        {
            var eventName = message.Body["params"]?["eventName"]?.Value<string>();
            if (string.IsNullOrEmpty(eventName))
            {
                _trace.TraceEvent(TraceEventType.Warning, 0,
                    "TelemetryEventInterceptor: telemetry/event without eventName; dropping.");
                return Task.FromResult(LspInterceptorResult.PassThrough);
            }

            var properties = new System.Collections.Generic.Dictionary<string, object>();
            var propsToken = message.Body["params"]?["properties"] as JObject;
            if (propsToken is not null)
            {
                foreach (var prop in propsToken.Properties())
                {
                    var value = prop.Value;
                    if (value is JValue jv && jv.Value is not null)
                        properties[prop.Name] = jv.Value;
                    else if (value is not null)
                        properties[prop.Name] = value.ToString();
                }
            }

            transmitter.TransmitEvent(new Reqnroll.IdeSupport.Common.Analytics.GenericEvent(eventName!, properties));

            _trace.TraceEvent(TraceEventType.Information, 0,
                "TelemetryEventInterceptor: Forwarded telemetry/event '{0}' ({1} props)",
                eventName, properties.Count);
        }
        catch (Exception ex)
        {
            _trace.TraceEvent(TraceEventType.Warning, 0,
                "TelemetryEventInterceptor: Error forwarding telemetry/event: {0}", ex.Message);
        }

        // Always pass through so the message continues to other interceptors and VS.
        return Task.FromResult(LspInterceptorResult.PassThrough);
    }
}
