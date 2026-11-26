using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    public sealed class CoreReadModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.CoreRead";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            // Core & Session (8)
            yield return new GetDocumentInfoCommand();
            yield return new OpenDocumentCommand();
            yield return new SaveDocumentCommand();
            yield return new RelinquishAllCommand();
            yield return new DocumentSessionCommand();
            yield return new FileInfoCommand();
            yield return new UndoCommand();
            yield return new VerifyChangesCommand();

            // Read, Navigation & Comparison (8)
            yield return new ListElementsCommand();
            yield return new QueryModelCommand();
            yield return new ModelScanCommand();
            yield return new ModelDiffCommand();
            yield return new NavigateCommand();
            yield return new CaptureViewCommand();
            yield return new SelectionExchangeCommand();
            yield return new QueryClassificationCommand();

            // Parameters, Delete & Plan Execution (6)
            yield return new ManageParametersCommand();
            yield return new WriteParamsVerifiedCommand();
            yield return new BindSharedParamCommand();
            yield return new DeleteVerifiedCommand();
            yield return new ExecutePlanCommand();
            yield return new CopyBetweenDocumentsCommand();

            // Template Kit & Precision Geometry (2)
            yield return new TemplateKitCommand();
            yield return new MeasureGeometryCommand();

            // Ola 4 — Topological Relations (1)
            yield return new QueryRelationsCommand();
        }
    }
}
