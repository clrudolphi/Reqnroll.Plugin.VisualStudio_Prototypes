using System.ComponentModel.Composition;
#nullable enable
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.VisualStudio.Diagnostics;

namespace Reqnroll.IdeSupport.VisualStudio.Analytics;

[Export(typeof(IAnalyticsTransmitter))]
public class AnalyticsTransmitter : Reqnroll.IdeSupport.Common.Analytics.AnalyticsTransmitter, IAsyncDisposable
{
    [ImportingConstructor]
    public AnalyticsTransmitter(
        IEnableAnalyticsChecker enableAnalyticsChecker,
        IUserUniqueIdStore userUniqueIdStore,
        IVersionProvider versionProvider,
        DeveroomCompositeLogger? logger = null)
        : base(CreateClient(userUniqueIdStore, versionProvider), enableAnalyticsChecker, logger)
    {
    }

    private static TelemetryClient CreateClient(IUserUniqueIdStore userStore, IVersionProvider versionProvider)
    {
        var config = new TelemetryConfiguration();
        var assembly = typeof(Reqnroll.IdeSupport.Common.Analytics.AnalyticsTransmitter).Assembly;
        using var stream = assembly.GetManifestResourceStream("Reqnroll.IdeSupport.Common.Analytics.InstrumentationKey.txt");
        using var reader = new StreamReader(stream);
        config.ConnectionString = reader.ReadLine();
        var client = new TelemetryClient(config);
        client.Context.User.Id = userStore.GetUserId();
        client.Context.User.AccountId = userStore.GetUserId();
        client.Context.GlobalProperties["Ide"] = "Microsoft Visual Studio";
        client.Context.GlobalProperties["IdeVersion"] = versionProvider.GetVsVersion();
        client.Context.GlobalProperties["ExtensionVersion"] = versionProvider.GetExtensionVersion();
        return client;
    }
}
