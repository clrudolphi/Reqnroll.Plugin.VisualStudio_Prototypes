namespace Reqnroll.VisualStudio.VsxStubs.ProjectSystem;

public class StubIdeScope : IIdeScope, IDisposable
{
    public StubIdeScope(ITestOutputHelper testOutputHelper)
    {
        AnalyticsTransmitter = new StubAnalyticsTransmitter(Logger);
    MonitoringService =
        new MonitoringService(
            AnalyticsTransmitter,
            Substitute.For<ITelemetryConfigurationHolder>());

        CompositeLogger.Add(new DeveroomXUnitLogger(testOutputHelper));
        CompositeLogger.Add(StubLogger);
        Actions = new StubIdeActions(this);

        SetupFireAndForget();
        SetupFireAndForgetOnBackgroundThread((action, callerName) =>
            BackGroundTasks = BackGroundTasks.Add($"{Interlocked.Increment(ref _taskId)}:{callerName}", action));

        CurrentTextView = Substitute.For<IWpfTextView>();
        TextViewFactory = (inputText, filePath) =>
            BasicTextViewFactory(inputText, filePath, VsContentTypes.FeatureFile);
        BackgroundTaskTokenSource = new DebuggableCancellationTokenSource(TimeSpan.FromSeconds(20));
    }

    public StubWpfTextView BasicTextViewFactory(TestText inputText, string filePath, string contentType)
    {
        return StubWpfTextView.CreateTextView(inputText, text =>
        {
            VsxStubObjects.Initialize();
            var projectScope = ProjectScopes.Single(p => p.FilesAdded.Any(f => f.Key == filePath));
            var textBuffer = VsxStubObjects.CreateTextBuffer(text.ToString(), contentType);
            textBuffer.Properties.AddProperty(typeof(IProjectScope), projectScope);
            textBuffer.Properties.AddProperty(typeof(IVsTextBuffer), new FilePathProvider(filePath));
            return textBuffer;
        });
    }

    public CancellationTokenSource BackgroundTaskTokenSource { get; }
    public StubAnalyticsTransmitter AnalyticsTransmitter { get; }
    public IDictionary<string, IWpfTextView> OpenViews { get; } = new Dictionary<string, IWpfTextView>();
    public StubLogger StubLogger { get; } = new();

    public DeveroomCompositeLogger CompositeLogger { get; } = new()
    {
        new DeveroomDebugLogger()
    };

    public StubWindowManager StubWindowManager { get; } = new();
    public List<InMemoryStubProjectScope> ProjectScopes { get; } = new();
    public IWpfTextView CurrentTextView { get; internal set; }
    public StubErrorListServices StubErrorListServices { get; } = new();

    public bool IsSolutionLoaded { get; } = true;

    public IProjectScope GetProject(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(typeof(IProjectScope),
            () => ProjectScopes.SingleOrDefault() as IProjectScope ?? new VoidProjectScope(this));

    public IDeveroomLogger Logger => CompositeLogger;
    public IIdeActions Actions { get; set; }
    public IDeveroomWindowManager WindowManager => StubWindowManager;
    public IFileSystemForIDE FileSystem { get; private set; } = new MockFileSystemForVs();

    public IDeveroomOutputPaneServices DeveroomOutputPaneServices { get; } =
        Substitute.For<IDeveroomOutputPaneServices>();

    public IDeveroomErrorListServices DeveroomErrorListServices => StubErrorListServices;
    public IMonitoringService MonitoringService { get; }

    public event EventHandler<EventArgs> WeakProjectsBuilt = null!;
    public event EventHandler<EventArgs> WeakProjectOutputsUpdated = null!;

    public void CalculateSourceLocationTrackingPositions(IEnumerable<SourceLocation> sourceLocations)
    {
    }

    public bool GetTextBuffer(SourceLocation sourceLocation, out ITextBuffer textBuffer)
    {
        if (OpenViews.TryGetValue(sourceLocation.SourceFile, out var view))
        {
            textBuffer = view.TextBuffer;
            return true;
        }

        textBuffer = Substitute.For<ITextBuffer>();
        return false;
    }

    public SyntaxTree GetSyntaxTree(ITextBuffer textBuffer)
    {
        var fileContent = textBuffer.CurrentSnapshot.GetText();
        return CSharpSyntaxTree.ParseText(fileContent);
    }

    private Action<Func<Task>, Action<Exception>, string> _fireAndForgetHandler;

    public void FireAndForget(Func<Task> action, Action<Exception> onException,
        [CallerMemberName] string callerName = "???")
        => _fireAndForgetHandler(action, onException, callerName);

    private void SetupFireAndForget()
    {
        _fireAndForgetHandler = (action, onException, _) =>
        {
            try
            {
                action().Wait();
            }
            catch (Exception e)
            {
                Logger.LogException(MonitoringService, e);
                onException(e);
            }
        };
    }

    private Action<Func<CancellationToken, Task>, string> _fireAndForgetOnBackgroundThreadHandler;

    public void FireAndForgetOnBackgroundThread(Func<CancellationToken, Task> action, string callerName = "???")
        => _fireAndForgetOnBackgroundThreadHandler(action, callerName);

    private volatile int _taskId;

    private ImmutableDictionary<string, Func<CancellationToken, Task>> BackGroundTasks { get; set; } =
        ImmutableDictionary<string, Func<CancellationToken, Task>>.Empty;

    public Task StartAndWaitAllBackgroundTasks()
    {
        var allTasks =
            BackGroundTasks.Values.Select(t => Task.Run(async () => await t(BackgroundTaskTokenSource.Token)));
        BackGroundTasks = BackGroundTasks.Clear();
        return Task.WhenAll(allTasks);
    }

    public void SetupFireAndForgetOnBackgroundThread(Action<Func<CancellationToken, Task>, string> callback)
    {
        _fireAndForgetOnBackgroundThreadHandler = callback;
    }

    public Task RunOnUiThreadAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public void OpenIfNotOpened(string path)
    {
        if (OpenViews.TryGetValue(path, out _))
            return;

        var lines = FileSystem.File.ReadAllLines(path);
        CreateTextView(new TestText(lines), path);
    }

    public IProjectScope[] GetProjectsWithFeatureFiles() => ProjectScopes.ToArray();

    public IDisposable CreateUndoContext(string undoLabel) => Substitute.For<IDisposable>();

    public IWpfTextView CreateTextView(TestText inputText, string filePath)
    {
        var textView = TextViewFactory(inputText, filePath);

        OpenViews[filePath] = textView;
        CurrentTextView = textView;
        return textView;
    }

    public Func<TestText, string, IWpfTextView> TextViewFactory;

    public IWpfTextView EnsureOpenTextView(SourceLocation sourceLocation)
    {
        if (OpenViews.TryGetValue(sourceLocation.SourceFile, out var view))
            return view;

        var lines = FileSystem.File.ReadAllLines(sourceLocation.SourceFile);
        var textView = CreateTextView(new TestText(lines), sourceLocation.SourceFile);
        return textView;
    }

    public void TriggerProjectsBuilt()
    {
        WeakProjectsBuilt?.Invoke(this, EventArgs.Empty);
        WeakProjectOutputsUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void UsePhysicalFileSystem()
    {
        FileSystem = new Reqnroll.IdeSupport.VisualStudio.FileSystemForVs();
    }

    public void Dispose()
    {
        BackgroundTaskTokenSource.Cancel();
        BackgroundTaskTokenSource.Dispose();
    }
}