using Reqnroll.IdeSupport.Common.Diagnostics;
using System;

namespace Reqnroll.IdeSupport.Common;

/// <summary>
/// Minimal IDE scope contract needed by the Wizards layer.
/// The full IIdeScope from Reqnroll.IdeSupport.VisualStudio has ~15 members (text buffers,
/// Roslyn, solution events, etc.) that wizards do not need.
/// </summary>
public interface IIdeScope
{
    bool IsSolutionLoaded { get; }
    IDeveroomLogger Logger { get; }
    IMonitoringService MonitoringService { get; }
    IIdeActions Actions { get; }
    IFileSystemForIDE FileSystem { get; }
}

/// <summary>
/// Minimal actions contract: only error/problem reporting used by wizards.
/// </summary>
public interface IIdeActions
{
    void ShowError(string description, Exception exception);
    void ShowProblem(string message);
}