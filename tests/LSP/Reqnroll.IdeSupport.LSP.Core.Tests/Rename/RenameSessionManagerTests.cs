#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Rename;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Rename;

public class RenameSessionManagerTests
{
    private static RenameSessionManager CreateSut() => new();

    [Fact]
    public void SetSession_then_TryConsume_returns_correct_attributeIndex()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 3);
        var consumed = sut.TryConsume("test.cs", 1, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(3);
    }

    [Fact]
    public void TryConsume_without_set_returns_false()
    {
        var sut = CreateSut();

        var consumed = sut.TryConsume("test.cs", 1, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void TryConsume_wrong_version_returns_false()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 3);
        var consumed = sut.TryConsume("test.cs", 2, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void TryConsume_consumed_only_once()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 7);
        var first = sut.TryConsume("test.cs", 1, out var firstIndex);
        var second = sut.TryConsume("test.cs", 1, out var secondIndex);

        first.Should().BeTrue();
        firstIndex.Should().Be(7);
        second.Should().BeFalse();
    }

    [Fact]
    public void Multiple_sessions_independent()
    {
        var sut = CreateSut();

        sut.SetSession("file-a.cs", 1, 10);
        sut.SetSession("file-b.cs", 2, 20);

        var consumedA = sut.TryConsume("file-a.cs", 1, out var indexA);
        var consumedB = sut.TryConsume("file-b.cs", 2, out var indexB);

        consumedA.Should().BeTrue();
        indexA.Should().Be(10);
        consumedB.Should().BeTrue();
        indexB.Should().Be(20);
    }

    /// <summary>
    /// Sessions expire after 30 seconds via the internal Cleanup method.
    /// Since we cannot fast-forward time in a unit test, this test verifies
    /// that a session is NOT prematurely removed by consuming immediately
    /// after setting (which succeeds). The 30-second expiry behaviour is
    /// covered by the implementation's Cleanup method, which is invoked
    /// on every SetSession and TryConsume call.
    /// </summary>
    [Fact]
    public void TryConsume_immediately_after_set_succeeds()
    {
        var sut = CreateSut();

        sut.SetSession("test.cs", 1, 5);
        var consumed = sut.TryConsume("test.cs", 1, out var attributeIndex);

        consumed.Should().BeTrue();
        attributeIndex.Should().Be(5);
    }
}
