using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.Documentation
{
    public sealed class DocumentationModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.Documentation";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            // Views & Sheets (8)
            yield return new CreateViewsCommand();
            yield return new ManageViewsCommand();
            yield return new ViewFiltersCommand();
            yield return new ViewTemplatesCommand();
            yield return new ManageSheetsCommand();
            yield return new SheetLayoutCommand();
            yield return new ExportPdfDwgCommand();
            yield return new PlanimetryPackCommand();

            // Schedules, Annotations & 2D (11)
            yield return new ManageSchedulesCommand();
            yield return new CreateScheduleCommand();
            yield return new CreateDimensionsCommand();
            yield return new ManageTagsCommand();
            yield return new CreateTextNotesCommand();
            yield return new CreateDetailLinesCommand();
            yield return new CreateFilledRegionsCommand();
            yield return new ManageKeynoteLegendsCommand();
            yield return new ManageCalloutsCommand();
            yield return new ManageScopeBoxesCommand();
            yield return new RevisionCloudsCommand();
        }
    }
}
