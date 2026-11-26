using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.AuditQa
{
    public sealed class AuditQaModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.AuditQa";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            yield return new AuditModelCommand();
            yield return new ClashDetectCommand();
            yield return new CheckClearancesCommand();
            yield return new QuantityTakeoffCommand();
            yield return new AuditWarningsCommand();
            yield return new AuditParametersCommand();
            yield return new ManageLinksCommand();
            yield return new CoordinationIssuesCommand();
            yield return new AuditSupportCommand();
            yield return new PurgeUnusedCommand();
            yield return new CodeCheckCommand();
            yield return new ChangeSurveyCommand();
        }
    }
}
