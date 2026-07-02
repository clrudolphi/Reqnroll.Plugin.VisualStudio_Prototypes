#nullable disable
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Diagnostics;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.LSP.Connector.Models;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Discovery;

namespace Reqnroll.IdeSupport.LSP.Server.Discovery;

public abstract class OutProcReqnrollConnector
{
    private const string BindingDiscoveryCommandName = "binding discovery";

    protected readonly DeveroomConfiguration _configuration;
    protected readonly string _extensionFolder;
    protected readonly IDeveroomLogger _logger;
    protected readonly IMonitoringService _monitoringService;
    protected readonly ProcessorArchitectureSetting _processorArchitecture;
    protected readonly ProjectSettings _projectSettings;
    protected readonly TargetFrameworkMoniker _targetFrameworkMoniker;
    protected NuGetVersion ReqnrollVersion => _projectSettings.ReqnrollVersion;

    protected OutProcReqnrollConnector(DeveroomConfiguration configuration, IDeveroomLogger logger,
        TargetFrameworkMoniker targetFrameworkMoniker, string extensionFolder,
        ProcessorArchitectureSetting processorArchitecture, ProjectSettings projectSettings,
        IMonitoringService monitoringService)
    {
        _configuration = configuration;
        _logger = logger;
        _targetFrameworkMoniker = targetFrameworkMoniker;
        _extensionFolder = extensionFolder;
        _processorArchitecture = processorArchitecture;
        _projectSettings = projectSettings;
        _monitoringService = monitoringService;
    }

    private bool DebugConnector => _configuration.DebugConnector ||
                                   Environment.GetEnvironmentVariable("DEVEROOM_DEBUGCONNECTOR") == "1";

    protected virtual string GetConnectorType()
    {
        return GetType().Name.Replace(nameof(OutProcReqnrollConnector), "");
    }

    public virtual DiscoveryResult RunDiscovery(string testAssemblyPath, string configFilePath)
    {
        var workingDirectory = Path.GetDirectoryName(testAssemblyPath);
        var arguments = new List<string>();
        var connectorPath = GetConnectorPath(arguments);
        arguments.Add("discovery");
        arguments.Add(testAssemblyPath);
        arguments.Add(configFilePath);
        if (DebugConnector)
            arguments.Add("--debug");

        if (connectorPath == null || !File.Exists(connectorPath))
            return new DiscoveryResult
            {
                ErrorMessage = $"Error during binding discovery. Unable to find connector: {connectorPath}",
                AnalyticsProperties = new Dictionary<string, object>(),
                ConnectorType = GetConnectorType()
            };

        var result = ProcessHelper.RunProcess(workingDirectory, connectorPath, arguments, encoding: Encoding.UTF8);

        _logger.LogVerbose($"{workingDirectory}>{connectorPath} {string.Join(" ", arguments)}");
        _logger.LogVerbose($"Exit code: {result.ExitCode}");
        if (result.HasErrors)
            _logger.LogWarning(result.StandardError);

#if DEBUG
        // Log only the JSON payload between the >>>>>>>>>> / <<<<<<<<<< markers; the assembly-loader
        // trace that precedes it is noise that bloats the log and buries the actual binding data.
        var jsonPayload = ExtractJsonPayload(result.StandardOut);
        if (jsonPayload != null)
            _logger.LogVerbose($"[Connector JSON]\n{jsonPayload}");
        else if (!string.IsNullOrWhiteSpace(result.StandardOut))
            _logger.LogVerbose($"[Connector stdout]\n{result.StandardOut}");
#endif

        DiscoveryResult discoveryResult;
        if (result.ExitCode != 0)
        {
            var errorMessage = result.HasErrors ? result.StandardError : "Unknown error.";

            discoveryResult = Deserialize(
                result,
                dr => GetDetailedErrorMessage(result, errorMessage + dr.ErrorMessage, BindingDiscoveryCommandName));
        }
        else
        {
            discoveryResult = Deserialize(
                result,
                dr => dr.IsFailed ? GetDetailedErrorMessage(result, dr.ErrorMessage, BindingDiscoveryCommandName) : dr.ErrorMessage!);
        }

        discoveryResult.ConnectorType = GetConnectorType();
        return discoveryResult;
    }

    private DiscoveryResult Deserialize(ProcessHelper.RunProcessResult result,
        Func<DiscoveryResult, string> formatErrorMessage)
    {
        DiscoveryResult discoveryResult;
        try
        {
            discoveryResult = ConnectorJsonSerialization.DeserializeObjectWithMarker<DiscoveryResult>(result.StandardOut)
                              ?? new DiscoveryResult
                              {
                                  ErrorMessage = $"Cannot deserialize: {result.StandardOut}",
                                  ConnectorType = GetConnectorType()
                              };
        }
        catch (Exception e)
        {
            discoveryResult = new DiscoveryResult
            {
                ErrorMessage = e.ToString(),
                ConnectorType = GetConnectorType()
            };
        }

        discoveryResult.ErrorMessage = formatErrorMessage(discoveryResult);
        discoveryResult.AnalyticsProperties ??= new Dictionary<string, object>();

        discoveryResult.AnalyticsProperties["ProjectTargetFramework"] = _targetFrameworkMoniker;
        discoveryResult.AnalyticsProperties["ProjectReqnrollVersion"] = ReqnrollVersion;
        if (_projectSettings.IsSpecFlowProject)             
            discoveryResult.AnalyticsProperties["LegacySpecFlow"] = true;
        discoveryResult.AnalyticsProperties["ConnectorType"] = discoveryResult.ConnectorType;
        discoveryResult.AnalyticsProperties["ConnectorArguments"] = result.Arguments;
        discoveryResult.AnalyticsProperties["ConnectorExitCode"] = result.ExitCode;
        if (!string.IsNullOrEmpty(discoveryResult.ReqnrollVersion))
            discoveryResult.AnalyticsProperties["ReqnrollVersion"] = discoveryResult.ReqnrollVersion;

        if (!string.IsNullOrEmpty(discoveryResult.ErrorMessage))
            discoveryResult.AnalyticsProperties["Error"] = discoveryResult.ErrorMessage;

        // DiscoveryResultEvent telemetry is not implemented in the LSP server; NullMonitoringService no-ops this.
        // _monitoringService.TransmitEvent(new DiscoveryResultEvent(discoveryResult));

        return discoveryResult;
    }

    private string GetDetailedErrorMessage(ProcessHelper.RunProcessResult result, string errorMessage, string command)
    {
        var exitCode = result.ExitCode < 0 ? "<not executed>" : result.ExitCode.ToString();
        return
            $"Error during {command}. {Environment.NewLine}Command executed:{Environment.NewLine}  {result.CommandLine}{Environment.NewLine}Exit code: {exitCode}{Environment.NewLine}Message: {Environment.NewLine}{errorMessage}";
    }

    protected abstract string GetConnectorPath(List<string> arguments);

    private static string ExtractJsonPayload(string stdout)
    {
        const string open  = ">>>>>>>>>>";
        const string close = "<<<<<<<<<<";
        var openIdx = stdout.IndexOf(open, StringComparison.Ordinal);
        if (openIdx < 0) return null;
        var afterOpen = stdout.IndexOf('\n', openIdx);
        if (afterOpen < 0) return null;
        var closeIdx = stdout.IndexOf(close, afterOpen, StringComparison.Ordinal);
        if (closeIdx < 0) return null;
        return stdout.Substring(afterOpen + 1, closeIdx - afterOpen - 1).Trim();
    }

    private string GetDotNetInstallLocation()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (_processorArchitecture == ProcessorArchitectureSetting.X86)
            programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrEmpty(programFiles))
            programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        return Path.Combine(programFiles!, "dotnet");
    }

    protected string GetDotNetExecCommand(List<string> arguments, string executableFolder, string executableFile)
    {
#if DEBUG
        _logger.LogInfo($"Invoking '{executableFile}'...");
#endif
        arguments.Add("exec");
        arguments.Add(Path.Combine(executableFolder, executableFile));
        return GetDotNetCommand();
    }

    private string GetDotNetCommand()
    {
        if (!OperatingSystem.IsWindows())
            return ResolveNonWindowsDotNetCommand(Environment.GetEnvironmentVariable("DOTNET_ROOT"));

        return Path.Combine(GetDotNetInstallLocation(), "dotnet.exe");
    }

    // No Windows-style Program Files layout on Linux/macOS. Prefer an explicit DOTNET_ROOT
    // (set by the .NET install scripts / CI images); otherwise rely on "dotnet" being
    // resolvable via PATH, which is the standard install on Linux/macOS.
    // Extracted as a pure function (taking the env var value as a parameter) so it can be
    // unit-tested without depending on the host OS actually being non-Windows.
    internal static string ResolveNonWindowsDotNetCommand(string dotNetRoot) =>
        string.IsNullOrEmpty(dotNetRoot) ? "dotnet" : Path.Combine(dotNetRoot, "dotnet");

    protected string GetConnectorsFolder()
    {
        var connectorsFolder = Path.Combine(_extensionFolder, "Connectors");
        if (Directory.Exists(connectorsFolder))
            return connectorsFolder;
        return _extensionFolder;
    }
}
