#nullable disable
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace Reqnroll.IdeSupport.Common.Tests.Analytics;

public class AnalyticsTransmitterTests
{
    private InMemoryTelemetryChannel _telemetryChannel;
    private IEnableAnalyticsChecker _enableAnalyticsCheckerStub;

    [Fact]
    public void Should_NotSendAnalytics_WhenDisabled()
    {
        var sut = CreateSut();
        GivenAnalyticsDisabled();

        sut.TransmitEvent(Substitute.For<IAnalyticsEvent>());

        _enableAnalyticsCheckerStub.Received(1).IsEnabled();
        _telemetryChannel.SentTelemtries.Should().BeEmpty();
    }

    [Fact]
    public void Should_SendAnalytics_WhenEnabled()
    {
        var sut = CreateSut();
        GivenAnalyticsEnabled();

        sut.TransmitEvent(Substitute.For<IAnalyticsEvent>());

        _enableAnalyticsCheckerStub.Received(1).IsEnabled();
        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Extension loaded")]
    [InlineData("Extension installed")]
    [InlineData("100 day usage")]
    public void Should_TransmitEvents(string eventName)
    {
        var sut = CreateSut();
        GivenAnalyticsEnabled();

        sut.TransmitEvent(new GenericEvent(eventName));

        _telemetryChannel.SentTelemtries.Should().HaveCount(1);
        _telemetryChannel.SentTelemtries.Single()
            .Should().BeOfType<EventTelemetry>()
            .Which.Name.Should().Be(eventName);
    }

    [Fact]
    public async Task Should_FlushOnDispose()
    {
        var sut = CreateSut();

        await sut.DisposeAsync();

        _telemetryChannel.IsFlushed.Should().BeTrue();
    }

    [Fact]
    public void Should_NotThrow_WhenAppInsightsFails()
    {
        var sut = CreateSut();
        GivenAnalyticsEnabled();

        _telemetryChannel.ThrowOnSend = true;

        var exception = Record.Exception(() => sut.TransmitEvent(Substitute.For<IAnalyticsEvent>()));

        Assert.Null(exception);
    }

    private void GivenAnalyticsEnabled()
    {
        _enableAnalyticsCheckerStub.IsEnabled().Returns(true);
    }

    private void GivenAnalyticsDisabled()
    {
        _enableAnalyticsCheckerStub.IsEnabled().Returns(false);
    }

    private AnalyticsTransmitter CreateSut()
    {
        _enableAnalyticsCheckerStub = Substitute.For<IEnableAnalyticsChecker>();
        _telemetryChannel = new InMemoryTelemetryChannel();
        var config = new TelemetryConfiguration
        {
            TelemetryChannel = _telemetryChannel,
            ConnectionString = $"InstrumentationKey={Guid.NewGuid():N}"
        };
        var telemetryClient = new TelemetryClient(config);
        return new AnalyticsTransmitter(telemetryClient, _enableAnalyticsCheckerStub);
    }
}

public class InMemoryTelemetryChannel : ITelemetryChannel
{
    public List<ITelemetry> SentTelemtries { get; } = new();
    public bool IsFlushed { get; private set; }
    public bool ThrowOnSend { get; set; }
    public bool? DeveloperMode { get; set; }
    public string EndpointAddress { get; set; }

    public void Send(ITelemetry item)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated AppInsights failure");
        SentTelemtries.Add(item);
    }

    public void Flush() => IsFlushed = true;
    public void Dispose() { }
}
