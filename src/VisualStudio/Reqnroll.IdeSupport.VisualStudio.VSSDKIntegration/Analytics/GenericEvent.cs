#nullable enable
using Reqnroll.IdeSupport.Common.Analytics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;

namespace Reqnroll.IdeSupport.VisualStudio.Analytics;

[Export(typeof(IAnalyticsEvent))]
public record GenericEvent : Reqnroll.IdeSupport.Common.Analytics.GenericEvent
{
    public GenericEvent(string eventName, IEnumerable<KeyValuePair<string, object>> properties) : base(eventName, properties) { }

    public GenericEvent(string eventName) : this(eventName, ImmutableDictionary<string, object>.Empty)
    {
    }
}
