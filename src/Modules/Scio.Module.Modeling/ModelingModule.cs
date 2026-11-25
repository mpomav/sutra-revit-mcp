using System.Collections.Generic;
using Scio.Revit.Sdk;

namespace Scio.Module.Modeling
{
    public sealed class ModelingModule : IRevitModule
    {
        public string ModuleName => "Scio.Module.Modeling";

        public IEnumerable<IRevitCommand> CreateCommands()
        {
            // Geometry & Architecture Elements (7)
            yield return new CreateElementsCommand();
            yield return new TransformElementsCommand();
            yield return new ManageCurtainCommand();
            yield return new SlabShapeCommand();
            yield return new FramingCommand();
            yield return new CreateRailingCommand();
            yield return new ManageAssembliesPartsCommand();
            yield return new JoinGeometryCommand();
            yield return new SplitByLevelCommand();
            yield return new WallFinishCommand();

            // Families & Catalog (4)
            yield return new CreateFamilyCommand();
            yield return new FamilyApplyCommand();
            yield return new SetKeynoteCommand();
            yield return new CatalogLookupCommand();

            // Model Configuration (8)
            yield return new ManageGroupsCommand();
            yield return new ManageWorksetsCommand();
            yield return new ManagePhasesCommand();
            yield return new ManageRevisionsCommand();
            yield return new ManageMaterialsCommand();
            yield return new ManageStylesCommand();
            yield return new ManageUnitsCommand();
            yield return new ManageSystemTypesCommand();
        }
    }
}
