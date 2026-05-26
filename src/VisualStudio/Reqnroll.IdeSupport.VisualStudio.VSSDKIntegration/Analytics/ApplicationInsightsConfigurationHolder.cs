using Microsoft.VisualStudio.ApplicationInsights.Channel;
using Microsoft.VisualStudio.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.Analytics;

[Export(typeof(ITelemetryConfigurationHolder))]
public class ApplicationInsightsConfigurationHolder : ITelemetryConfigurationHolder
{
    private readonly IReqnrollContextInitializer _contextInitializer;

    [ImportingConstructor]
    public ApplicationInsightsConfigurationHolder(IReqnrollContextInitializer contextInitializer)
    {
        _contextInitializer = contextInitializer;
    }

    public void ApplyConfiguration()
    {
        using (var stream = typeof(ITelemetryConfigurationHolder).Assembly.GetManifestResourceStream(
                   "Reqnroll.IdeSupport.Common.Analytics.InstrumentationKey.txt"))
        {
            using (var reader = new StreamReader(stream))
            {
                var instrumentationKey = reader.ReadLine();

                TelemetryConfiguration.Active.InstrumentationKey = instrumentationKey;
                TelemetryConfiguration.Active.TelemetryChannel = new InMemoryChannel();
                TelemetryConfiguration.Active.ContextInitializers.Add(_contextInitializer);
            }
        }
    }
}
