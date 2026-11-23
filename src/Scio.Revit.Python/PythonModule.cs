using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Revit.Python
{
    public sealed class PythonModule : IRevitModule
    {
        public string ModuleName => "Scio.Revit.Python";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new ExecutePythonCommand();
        }
    }
}
