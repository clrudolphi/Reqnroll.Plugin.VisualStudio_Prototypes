using System.ComponentModel.Composition;
using Reqnroll.IdeSupport.Common.Analytics;

namespace Reqnroll.IdeSupport.VisualStudio.SDKIntegration.Analytics;


[Export(typeof(IEnableAnalyticsChecker))]
public class EnableAnalyticsChecker : Reqnroll.IdeSupport.Common.Analytics.EnableAnalyticsChecker { }
