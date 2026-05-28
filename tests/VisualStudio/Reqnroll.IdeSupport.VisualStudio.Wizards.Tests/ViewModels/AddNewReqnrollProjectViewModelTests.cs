namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.ViewModels;

public class AddNewReqnrollProjectViewModelTests
{
    [Fact]
    public void Default_DotNetFramework_is_net8()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        vm.DotNetFramework.Should().Be("net8.0");
    }

    [Fact]
    public void Default_UnitTestFramework_is_MSTest()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        vm.UnitTestFramework.Should().Be("MsTest");
    }

    [Theory]
    [InlineData("net481", true)]
    [InlineData("net472", true)]
    [InlineData("net48", true)]
    [InlineData("net8.0", false)]
    [InlineData("net9.0", false)]
    [InlineData("net10.0", false)]
    public void IsNetFramework_is_correct_for_given_framework(string framework, bool expected)
    {
        var vm = new AddNewReqnrollProjectViewModel { DotNetFramework = framework };
        vm.IsNetFramework.Should().Be(expected);
    }

    [Fact]
    public void TestFrameworks_collection_is_populated()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        vm.TestFrameworks.Should().NotBeEmpty();
        vm.TestFrameworks.Should().Contain("MSTest");
        vm.TestFrameworks.Should().Contain("NUnit");
        vm.TestFrameworks.Should().Contain("xUnit");
    }

    [Fact]
    public void Setting_DotNetFramework_raises_PropertyChanged_for_TestFrameworks()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.DotNetFramework = "net481";

        changedProperties.Should().Contain(nameof(AddNewReqnrollProjectViewModel.TestFrameworks));
    }

    [Fact]
    public void Setting_DotNetFramework_to_same_value_still_raises_PropertyChanged()
    {
        var vm = new AddNewReqnrollProjectViewModel();
        var raised = false;
        vm.PropertyChanged += (_, _) => raised = true;

        vm.DotNetFramework = vm.DotNetFramework;

        raised.Should().BeTrue();
    }
}
