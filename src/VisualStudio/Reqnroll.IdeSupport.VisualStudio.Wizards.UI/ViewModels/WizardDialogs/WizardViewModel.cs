// Ported from Reqnroll.VisualStudio\UI\ViewModels\WizardDialogs\WizardViewModel.cs
// DelegateCommand (VS SDK) replaced with RelayCommand (portable).

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

public class WizardViewModel : INotifyPropertyChanged
{
    public WizardViewModel(string finishButtonLabel, string dialogTitle, params WizardPageViewModel[] pages)
    {
        VisitedPages = new HashSet<WizardPageViewModel>();
        FinishButtonLabel = finishButtonLabel;
        DialogTitle = dialogTitle;
        NextCommand = new RelayCommand(_ => MovePageBy(1), _ => CanMovePageBy(1));
        PreviousCommand = new RelayCommand(_ => MovePageBy(-1), _ => CanMovePageBy(-1));
        Pages = new ObservableCollection<WizardPageViewModel>();
        if (pages != null)
            foreach (var page in pages)
                Pages.Add(page);
        if (Pages.Count > 0)
            MoveToPage(0);
    }

    public string DialogTitle { get; }
    public string FinishButtonLabel { get; }
    public ObservableCollection<WizardPageViewModel> Pages { get; }
    public HashSet<WizardPageViewModel> VisitedPages { get; }

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }

    public WizardPageViewModel? ActivePage => Pages.FirstOrDefault(p => p.IsActive);

    public int ActivePageIndex
    {
        get
        {
            var activePage = ActivePage;
            return activePage == null ? 0 : Pages.IndexOf(activePage);
        }
    }

    public bool IsOnLastPage => ActivePageIndex == Pages.Count - 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool CanMovePageBy(int step)
    {
        int newPageIndex = ActivePageIndex + step;
        return newPageIndex >= 0 && newPageIndex < Pages.Count;
    }

    private void MovePageBy(int step)
    {
        MoveToPage(ActivePageIndex + step, ActivePageIndex);
    }

    private void MoveToPage(int newPageIndex, int activePageIndex = -1)
    {
        if (activePageIndex < 0)
            activePageIndex = ActivePageIndex;
        if (newPageIndex < 0 || newPageIndex >= Pages.Count)
            return;
        Pages[activePageIndex].IsActive = false;
        var newPage = Pages[newPageIndex];
        newPage.IsActive = true;
        VisitedPages.Add(newPage);
        OnPropertyChanged(nameof(ActivePage));
        OnPropertyChanged(nameof(ActivePageIndex));
        OnPropertyChanged(nameof(IsOnLastPage));
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
