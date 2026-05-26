#nullable disable

using System.Collections.Generic;

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

public interface IDeveroomErrorListServices
{
    void ClearErrors(DeveroomUserErrorCategory category);
    void AddErrors(IEnumerable<DeveroomUserError> errors);
}

public enum DeveroomUserErrorCategory
{
    Discovery
}

public class DeveroomUserError
{
    public string Message { get; set; }
    //public SourceLocation SourceLocation { get; set; }
    public DeveroomUserErrorCategory Category { get; set; }
}
