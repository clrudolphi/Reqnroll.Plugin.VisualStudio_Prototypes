using Reqnroll.IdeSupport.Common.Configuration;

namespace Reqnroll.IdeSupport.LSP.Server.Configuration;

/// <summary>
/// EditorConfig settings resolved for a specific file path, backed by a flat key→value
/// dictionary built by <see cref="FileSystemEditorConfigOptionsProvider"/>.
/// </summary>
internal sealed class FileSystemEditorConfigOptions : IEditorConfigOptions
{
    private readonly Dictionary<string, string> _values;

    internal FileSystemEditorConfigOptions(Dictionary<string, string> values)
    {
        _values = values;
    }

    public TResult GetOption<TResult>(string key, TResult defaultValue)
    {
        if (!_values.TryGetValue(key, out var raw))
            return defaultValue;

        if (typeof(TResult) == typeof(bool))
            return (TResult)(object)(raw.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (typeof(TResult) == typeof(int) && int.TryParse(raw, out var i))
            return (TResult)(object)i;
        if (typeof(TResult) == typeof(string))
            return (TResult)(object)raw;

        return defaultValue;
    }

    public bool GetBoolOption(string key, bool defaultValue) => GetOption(key, defaultValue);
}
