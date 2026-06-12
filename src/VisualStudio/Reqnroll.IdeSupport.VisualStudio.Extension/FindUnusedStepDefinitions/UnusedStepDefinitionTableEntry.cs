using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;

/// <summary>
/// A single row in the Find All References table window, representing one unused
/// step-definition binding.
/// </summary>
internal sealed class UnusedStepDefinitionTableEntry : ITableEntry
{
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

    public object Identity => _values;

    public bool CanSetValue(string keyName) => true;

    public bool TryGetValue(string keyName, out object content) =>
        _values.TryGetValue(keyName, out content!);

    public bool TrySetValue(string keyName, object content)
    {
        _values[keyName] = content;
        return true;
    }
}
