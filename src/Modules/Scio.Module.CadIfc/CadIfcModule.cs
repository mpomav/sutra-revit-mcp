using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.CadIfc
{
    public sealed class CadIfcModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.CadIfc";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new CadInspectCommand();
            yield return new CadLinkCommand();
            yield return new CadToWallsCommand();
            yield return new CadToGridsCommand();
            yield return new DeliverIfcCommand();
            yield return new IfcAuditCommand();
            yield return new IdsCheckCommand();
            yield return new GeoreferenceCommand();
            yield return new ScanToBimCommand();
            yield return new ExportMeshCommand();
            yield return new ManageImagesCommand();
        }
    }
}
