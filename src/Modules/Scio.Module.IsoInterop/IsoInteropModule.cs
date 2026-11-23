using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.IsoInterop
{
    public sealed class IsoInteropModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.IsoInterop";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new ProjectContextCommand();
            yield return new InformationContainerCommand();
            yield return new CdeStatusCommand();
            yield return new ExchangeManifestCommand();
            yield return new ExportExcelCsvCommand();
            yield return new ImportCsvParamsCommand();
            yield return new BudgetTakeoffCommand();
            yield return new PowerBiExportCommand();
            yield return new CobieExportCommand();
            yield return new NwcFbxExportCommand();
            yield return new CheckIsoNamingCommand();
            yield return new DeliverPackageCommand();
        }
    }
}
