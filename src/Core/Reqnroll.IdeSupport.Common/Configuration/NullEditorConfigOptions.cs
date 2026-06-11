namespace Reqnroll.IdeSupport.Common.Configuration;

/// <summary>
/// Returns the caller-supplied default for every key. Used when no .editorconfig applies.
/// </summary>
public sealed class NullEditorConfigOptions : IEditorConfigOptions
{
    public static readonly NullEditorConfigOptions Instance = new();

    private NullEditorConfigOptions() { }

    public TResult GetOption<TResult>(string key, TResult defaultValue) => defaultValue;
    public bool GetBoolOption(string key, bool defaultValue) => defaultValue;
}
