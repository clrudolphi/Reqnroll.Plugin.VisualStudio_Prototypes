#nullable enable
using Reqnroll.IdeSupport.Common.Analytics;
using Reqnroll.IdeSupport.VisualStudio.Diagnostics;
using System.ComponentModel.Composition;
using System.Net.Http;

namespace Reqnroll.IdeSupport.VisualStudio.Analytics;

[Export(typeof(IAnalyticsTransmitter))]
public class AnalyticsTransmitter : Reqnroll.IdeSupport.Common.Analytics.AnalyticsTransmitter
{

    [ImportingConstructor]
    public AnalyticsTransmitter(IAnalyticsTransmitterSink analyticsTransmitterSink,
        IEnableAnalyticsChecker enableAnalyticsChecker, DeveroomCompositeLogger? logger = null) : base(analyticsTransmitterSink, enableAnalyticsChecker, logger) { }

}
