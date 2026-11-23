using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.Mep
{
    public sealed class MepModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.Mep";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new MepCreatePipeCommand();
            yield return new MepCreateDuctCommand();
            yield return new MepCreateConduitTrayCommand();
            yield return new MepSystemsCommand();
            yield return new MepConnectorsCommand();
            yield return new MepSlopeCheckCommand();
            yield return new MepElectricalCommand();
        }
    }
}
