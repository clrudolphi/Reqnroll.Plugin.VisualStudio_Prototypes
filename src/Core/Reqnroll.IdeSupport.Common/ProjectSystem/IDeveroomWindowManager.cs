using System;
using System.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.ProjectSystem;

public interface IDeveroomWindowManager
{
    bool? ShowDialog<TViewModel>(TViewModel viewModel);
}
