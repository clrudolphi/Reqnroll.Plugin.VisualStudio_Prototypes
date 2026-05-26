// Ported from Reqnroll.VisualStudio\UI\ViewModels\AddNewReqnrollProjectViewModel.cs
#nullable disable

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels;

public class AddNewReqnrollProjectViewModel : INotifyPropertyChanged
{
    private const string MsTest = "MsTest";
    private const string Net8 = "net8.0";
    private const string Net9 = "net9.0";
    private const string Net10 = "net10.0";

#if DEBUG
    public static AddNewReqnrollProjectViewModel DesignData = new()
    {
        DotNetFramework = Net9,
        UnitTestFramework = MsTest,
    };
#endif

    private string _dotNetFramework = Net8;

    public string DotNetFramework
    {
        get => _dotNetFramework;
        set
        {
            _dotNetFramework = value;
            OnPropertyChanged(nameof(TestFrameworks));
        }
    }

    public bool IsNetFramework =>
        DotNetFramework.StartsWith("net4", StringComparison.InvariantCultureIgnoreCase);

    public string UnitTestFramework { get; set; } = MsTest;


    public ObservableCollection<string> TestFrameworks { get; } =
        new(new List<string> { "MSTest", "NUnit", "xUnit", "xUnit.v3", "TUnit" });

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
