// Ported from Reqnroll.VisualStudio\UI\ViewModels\WizardDialogs\WizardPageViewModel.cs
namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

public class WizardPageViewModel : INotifyPropertyChanged
{
    private bool _isActive;

    public WizardPageViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value == _isActive) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
