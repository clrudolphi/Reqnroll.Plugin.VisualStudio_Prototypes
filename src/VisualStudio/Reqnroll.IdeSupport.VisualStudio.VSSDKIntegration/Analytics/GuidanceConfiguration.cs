#nullable disable
using Reqnroll;
using Reqnroll.IdeSupport.Common.Analytics;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.SDKIntegration.Analytics;

[Export(typeof(IGuidanceConfiguration))]
public class GuidanceConfiguration : Reqnroll.IdeSupport.Common.Analytics.GuidanceConfiguration
{

}
