using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Reqnroll.IdeSupport.Common.Diagnostics;

namespace Reqnroll.IdeSupport.Common.Analytics;

public class AnalyticsTransmitter : IAnalyticsTransmitter, IAsyncDisposable
{
    private readonly TelemetryClient _telemetryClient;
    private readonly IEnableAnalyticsChecker _enableAnalyticsChecker;
    private readonly IDeveroomLogger? _logger;

    public AnalyticsTransmitter(TelemetryClient telemetryClient,
        IEnableAnalyticsChecker enableAnalyticsChecker, IDeveroomLogger? logger = null)
    {
        _telemetryClient = telemetryClient;
        _enableAnalyticsChecker = enableAnalyticsChecker;
        _logger = logger;
    }

    public void TransmitEvent(IAnalyticsEvent analyticsEvent)
    {
        try
        {
            DumpAnalyticsEvent(analyticsEvent);
            if (!_enableAnalyticsChecker.IsEnabled()) return;

            var eventTelemetry = new EventTelemetry(analyticsEvent.EventName) { Timestamp = DateTime.UtcNow };
            foreach (var property in analyticsEvent.Properties)
            {
                eventTelemetry.Properties.Add(property.Key, property.Value?.ToString() ?? string.Empty);
            }
            _telemetryClient.TrackEvent(eventTelemetry);
        }
        catch (Exception ex)
        {
            TransmitExceptionEvent(ex, ImmutableDictionary<string, object>.Empty);
        }
    }

    public void TransmitExceptionEvent(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        var isNormalError = IsNormalError(exception);
        if (isNormalError)
            TransmitException(exception, additionalProps);
        else
            TransmitFatalExceptionEvent(exception, true);
    }

    public void TransmitFatalExceptionEvent(Exception exception, bool isFatal)
    {
        var additionalProps = ImmutableDictionary.CreateBuilder<string, object>();
        if (isFatal)
            additionalProps.Add("IsFatal", isFatal.ToString());

        TransmitException(exception, additionalProps.ToImmutable());
    }

    private void TransmitException(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        try
        {
            var additionalPropsArray = additionalProps.ToArray();
            DumpAnalyticsException(exception, additionalPropsArray);

            var exceptionTelemetry = new ExceptionTelemetry(exception) { Timestamp = DateTime.UtcNow };
            foreach (var prop in additionalPropsArray)
            {
                exceptionTelemetry.Properties.Add(prop.Key, prop.Value?.ToString() ?? string.Empty);
            }
            _telemetryClient.TrackException(exceptionTelemetry);
        }
        catch (Exception ex)
        {
            // catch all exceptions since we do not want to break the whole extension simply because data transmission failed
            Debug.WriteLine(ex, "Error during transmitting analytics event.");
        }
    }

    [Conditional("ANALYTICS_DEBUG")]
    private void DumpAnalyticsEvent(IAnalyticsEvent analyticsEvent)
    {
        _logger?.LogVerbose(() => $"{analyticsEvent.EventName}: {string.Join(Environment.NewLine + "  ", analyticsEvent.Properties.Select(p => $"{p.Key}={p.Value}"))}");
    }

    [Conditional("ANALYTICS_DEBUG")]
    private void DumpAnalyticsException(Exception exception, IEnumerable<KeyValuePair<string, object>> additionalProps)
    {
        _logger?.LogVerbose(() => $"{exception.Message}: {string.Join(Environment.NewLine + "  ", additionalProps.Select(p => $"{p.Key}={p.Value}"))}");
    }

    private static bool IsNormalError(Exception exception)
    {
        if (exception is AggregateException aggregateException)
            return aggregateException.InnerExceptions.All(IsNormalError);
        return
            //exception is DeveroomConfigurationException ||
            exception is TimeoutException ||
            exception is TaskCanceledException ||
            exception is OperationCanceledException ||
            exception is HttpRequestException;
    }

    public async ValueTask DisposeAsync()
    {
        _telemetryClient.Flush();
        await Task.Delay(1000);
    }
}
