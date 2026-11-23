using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.Structure
{
    public sealed class StructureModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.Structure";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new QueryStructureCommand();
            yield return new RebarInspectCommand();
            yield return new RebarCreateCommand();
            yield return new RebarCoverCommand();
            yield return new StructuralConnectionsCommand();
            yield return new AnalyticalAuditCommand();
            yield return new StructuralScheduleParserCommand();
        }
    }
}
