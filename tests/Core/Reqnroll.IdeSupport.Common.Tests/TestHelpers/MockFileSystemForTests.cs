using System.IO.Abstractions.TestingHelpers;

namespace Reqnroll.IdeSupport.Common.Tests.TestHelpers;

/// <summary>
/// Minimal MockFileSystem that also implements IFileSystemForIDE, for use in Common.Tests
/// without any VSSDK or WPF dependencies.
/// </summary>
internal class MockFileSystemForTests : MockFileSystem, IFileSystemForIDE
{
}
