namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.ViewModels;

public class WelcomeDialogViewModelTests
{
    [Fact]
    public void Constructor_creates_three_pages()
    {
        var vm = new WelcomeDialogViewModel();
        vm.Pages.Should().HaveCount(3);
    }

    [Fact]
    public void First_page_is_active_after_construction()
    {
        var vm = new WelcomeDialogViewModel();
        vm.ActivePageIndex.Should().Be(0);
        vm.ActivePage.Should().NotBeNull();
    }

    [Fact]
    public void NextCommand_advances_to_second_page()
    {
        var vm = new WelcomeDialogViewModel();

        vm.NextCommand.Execute(null);

        vm.ActivePageIndex.Should().Be(1);
    }

    [Fact]
    public void PreviousCommand_is_disabled_on_first_page()
    {
        var vm = new WelcomeDialogViewModel();
        vm.PreviousCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void NextCommand_is_disabled_on_last_page()
    {
        var vm = new WelcomeDialogViewModel();
        // Navigate to last page
        while (vm.NextCommand.CanExecute(null))
            vm.NextCommand.Execute(null);

        vm.NextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void IsOnLastPage_is_true_after_navigating_to_last_page()
    {
        var vm = new WelcomeDialogViewModel();
        while (vm.NextCommand.CanExecute(null))
            vm.NextCommand.Execute(null);

        vm.IsOnLastPage.Should().BeTrue();
    }

    [Fact]
    public void PreviousCommand_navigates_back_from_second_page()
    {
        var vm = new WelcomeDialogViewModel();
        vm.NextCommand.Execute(null); // go to page 2

        vm.PreviousCommand.Execute(null); // go back to page 1

        vm.ActivePageIndex.Should().Be(0);
    }

    [Fact]
    public void DialogTitle_is_set()
    {
        var vm = new WelcomeDialogViewModel();
        vm.DialogTitle.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void FinishButtonLabel_is_set()
    {
        var vm = new WelcomeDialogViewModel();
        vm.FinishButtonLabel.Should().NotBeNullOrEmpty();
    }
}
