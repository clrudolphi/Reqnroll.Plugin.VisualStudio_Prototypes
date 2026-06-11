namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// No-op provider that always returns <see cref="NullEditorConfigOptions"/>. Used in contexts
/// where .editorconfig lookup is unavailable (tests, non-LSP hosts).
/// </summary>
public sealed class NullEditorConfigOptionsProvider : IEditorConfigOptionsProvider
{
    public static readonly NullEditorConfigOptionsProvider Instance = new();

    private NullEditorConfigOptionsProvider() { }

    public IEditorConfigOptions GetEditorConfigOptionsByPath(string filePath)
        => NullEditorConfigOptions.Instance;

    public void InvalidateCache(string editorConfigFilePath) { }
}
