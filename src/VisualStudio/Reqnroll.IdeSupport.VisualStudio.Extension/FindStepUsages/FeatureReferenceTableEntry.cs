using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.TableManager;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.FindStepUsages;

/// <summary>
/// A single row in the Find All References table window, representing one feature-file step
/// that matches the queried binding.
/// </summary>
internal sealed class FeatureReferenceTableEntry : ITableEntry
{
    private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

    /// <summary>Unique identity used by the table manager for row deduplication.</summary>
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
