using System;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio;

public interface IVersionProvider
{
    string GetVsVersion();
    string GetExtensionVersion();
}
