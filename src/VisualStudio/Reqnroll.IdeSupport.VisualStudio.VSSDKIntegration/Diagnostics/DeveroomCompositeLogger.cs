using Reqnroll.IdeSupport.Common.Diagnostics;
using System.Collections;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.Diagnostics;

[Export(typeof(IDeveroomLogger))]
[Export(typeof(DeveroomCompositeLogger))]
public class DeveroomCompositeLogger : Reqnroll.IdeSupport.Common.Diagnostics.DeveroomCompositeLogger
{
  
}