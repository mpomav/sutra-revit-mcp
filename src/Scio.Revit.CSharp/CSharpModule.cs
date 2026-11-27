using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Revit.CSharp
{
    public sealed class CSharpModule : IRevitModule
    {
        public string ModuleName => "Scio.Revit.CSharp";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new ExecuteCSharpCommand();
        }
    }
}
