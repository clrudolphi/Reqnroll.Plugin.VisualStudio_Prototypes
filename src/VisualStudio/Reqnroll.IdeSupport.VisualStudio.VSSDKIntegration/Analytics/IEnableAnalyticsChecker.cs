using Reqnroll.IdeSupport.Common.Analytics;
using System;
using System.ComponentModel.Composition;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.SDKIntegration.Analytics;


[Export(typeof(IEnableAnalyticsChecker))]
public class EnableAnalyticsChecker : Reqnroll.IdeSupport.Common.Analytics.EnableAnalyticsChecker { }
