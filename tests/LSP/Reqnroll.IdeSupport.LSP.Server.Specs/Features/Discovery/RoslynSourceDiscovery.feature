Feature: Roslyn source-level binding discovery

Editing a C# step-definition file updates step binding state immediately, without a build.
The server registers interest in .cs files and re-discovers their bindings via Roslyn on every
edit, then re-matches open feature files so unbound steps surface right away (design doc F2).

Scenario: A bound step becomes unbound when its binding expression is edited away in source
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class CalculatorSteps
			{
				[Given("the first number is (.*)")]
				public void GivenTheFirstNumberIs(int number) { }
			}
		}
		"""
	And the feature file "Calculator.feature" is opened with
		"""
		Feature: Calculator

		Scenario: Add
			Given the first number is 50
		"""
	Then the feature step "the first number is 50" is reported as bound
	When the C# step definition file "CalculatorSteps.cs" is changed to
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class CalculatorSteps
			{
				[Given("the second number is (.*)")]
				public void GivenTheSecondNumberIs(int number) { }
			}
		}
		"""
	Then the feature step "the first number is 50" is reported as unbound

Scenario: An unbound step becomes bound when its binding expression is fixed in source
	Given the LSP server is started
	When the project is announced with output assembly "Sample.dll" for "Calculator.feature"
	And the C# step definition file "CalculatorSteps.cs" is opened with
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class CalculatorSteps
			{
				[Given("the second number is (.*)")]
				public void GivenTheSecondNumberIs(int number) { }
			}
		}
		"""
	And the feature file "Calculator.feature" is opened with
		"""
		Feature: Calculator

		Scenario: Add
			Given the first number is 50
		"""
	Then the feature step "the first number is 50" is reported as unbound
	When the C# step definition file "CalculatorSteps.cs" is changed to
		"""
		using Reqnroll;
		namespace Sample
		{
			[Binding]
			public class CalculatorSteps
			{
				[Given("the first number is (.*)")]
				public void GivenTheFirstNumberIs(int number) { }
			}
		}
		"""
	Then the feature step "the first number is 50" is reported as bound
