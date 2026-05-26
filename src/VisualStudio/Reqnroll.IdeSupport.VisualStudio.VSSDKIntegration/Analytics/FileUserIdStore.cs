using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Analytics;
using System;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.SDKIntegration.Analytics;


[Export(typeof(IUserUniqueIdStore))]
public class FileUserIdStore : IUserUniqueIdStore
{
    public static readonly string UserIdFilePath = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Reqnroll\userid");
    private readonly IFileSystemForIDE _fileSystem;

    private readonly Lazy<string> _lazyUniqueUserId;

    [ImportingConstructor]
    public FileUserIdStore(IFileSystemForIDE fileSystem)
    {
        _fileSystem = fileSystem;
        _lazyUniqueUserId = new Lazy<string>(FetchAndPersistUserId);
    }

    public string GetUserId() => _lazyUniqueUserId.Value;

    private string FetchAndPersistUserId()
    {
        if (_fileSystem.File.Exists(UserIdFilePath))
        {
            var userIdStringFromFile = _fileSystem.File.ReadAllText(UserIdFilePath);
            if (IsValidGuid(userIdStringFromFile)) return userIdStringFromFile;
        }

        return GenerateAndPersistUserId();
    }

    private void PersistUserId(string userId)
    {
        var directoryName = Path.GetDirectoryName(UserIdFilePath);
        if (!_fileSystem.Directory.Exists(directoryName)) _fileSystem.Directory.CreateDirectory(directoryName);

        _fileSystem.File.WriteAllText(UserIdFilePath, userId);
    }

    private bool IsValidGuid(string guid) => Guid.TryParse(guid, out var parsedGuid);

    private string GenerateAndPersistUserId()
    {
        var newUserId = Guid.NewGuid().ToString();

        PersistUserId(newUserId);

        return newUserId;
    }
}
