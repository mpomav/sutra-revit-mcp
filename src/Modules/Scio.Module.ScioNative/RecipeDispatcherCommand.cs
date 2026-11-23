using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scio.Contracts;
using Scio.Revit.Core;

namespace Scio.Revit.Commands
{
    public sealed class RecipeDispatcherCommand : ICommand
    {
        private static readonly HashSet<string> AllowedRecipes = new HashSet<string>(StringComparer.Ordinal)
        {
            "scio_split_floor_loops",
            "scio_split_multilayer_walls",
            "scio_split_multilayer_slabs",
            "scio_ungroup_and_mark",
            "scio_regroup_by_param",
            "scio_copy_slab_elevations",
            "scio_embed_floors_in_toposolid",
            "scio_grade_toposolid_around_floors",
            "scio_rectangularize_walls"
        };

        private static readonly string[] SafetyEnvelopeKeys =
        {
            "target_document", "dry_run", "confirmation_token", "idempotency_key"
        };

        private readonly Func<string, ICommand> _resolve;

        public RecipeDispatcherCommand(Func<string, ICommand> resolve)
        {
            _resolve = resolve;
        }

        public string Name => "scio_recipe";

        public string Description =>
            "Despachador unificado de recetas de modelado SCIO. Ejecuta una receta por nombre " +
            "(split_floor_loops, split_multilayer_walls, split_multilayer_slabs, ungroup_and_mark, " +
            "regroup_by_param, copy_slab_elevations, embed_floors_in_toposolid, grade_toposolid_around_floors, " +
            "rectangularize_walls) pasando sus parametros con soporte de dry_run y verificacion tipada.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Scio.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string recipeName = request.Value<string>("recipe") ?? request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(recipeName))
            {
                return CommandResult.Fail(
                    "Missing 'recipe' parameter. Available recipes: split_floor_loops, split_multilayer_walls, " +
                    "split_multilayer_slabs, ungroup_and_mark, regroup_by_param, copy_slab_elevations, " +
                    "embed_floors_in_toposolid, grade_toposolid_around_floors, rectangularize_walls.");
            }

            string normalized = recipeName.Trim().ToLowerInvariant();
            string targetToolName = normalized.StartsWith("scio_", StringComparison.Ordinal)
                ? normalized
                : "scio_" + normalized;

            if (!AllowedRecipes.Contains(targetToolName))
            {
                return CommandResult.Fail(
                    $"Recipe '{recipeName}' is not in the scio_recipe allow-list. " +
                    "Allowed recipes: split_floor_loops, split_multilayer_walls, split_multilayer_slabs, " +
                    "ungroup_and_mark, regroup_by_param, copy_slab_elevations, embed_floors_in_toposolid, " +
                    "grade_toposolid_around_floors, rectangularize_walls. Nothing ran.");
            }

            CommandContract childContract = Contract.Find(targetToolName);
            if (childContract == null)
            {
                return CommandResult.Fail($"Recipe contract '{targetToolName}' was not found. Nothing ran.");
            }

            if (!Settings.IsToolAllowed(childContract, out string permissionReason))
            {
                return CommandResult.Fail(permissionReason + " Nothing ran.");
            }

            JObject forwardedArgs;
            if (request["arguments"] != null)
            {
                if (!(request["arguments"] is JObject nested))
                {
                    return CommandResult.Fail("Parameter 'arguments' must be a JSON object when provided. Nothing ran.");
                }
                forwardedArgs = (JObject)nested.DeepClone();
            }
            else
            {
                forwardedArgs = (JObject)request.DeepClone();
                forwardedArgs.Remove("recipe");
                forwardedArgs.Remove("name");
                forwardedArgs.Remove("arguments");
            }

            // Detect conflicting top-level vs nested safety flags before forwarding.
            if (request["dry_run"] != null && forwardedArgs["dry_run"] != null &&
                request.Value<bool?>("dry_run") != forwardedArgs.Value<bool?>("dry_run"))
            {
                return CommandResult.Fail(
                    "Conflicting 'dry_run' values between top-level request and 'arguments'. Nothing ran.");
            }

            foreach (string key in SafetyEnvelopeKeys)
            {
                if (request[key] != null && forwardedArgs[key] == null)
                {
                    forwardedArgs[key] = request[key].DeepClone();
                }
            }

            bool effectiveDryRun = forwardedArgs["dry_run"]?.Type == JTokenType.Boolean
                ? forwardedArgs.Value<bool>("dry_run")
                : true;

            if (!effectiveDryRun && Settings.ForceReadOnlyOnWorkshared)
            {
                bool? workshared = null;
                try { workshared = app?.ActiveUIDocument?.Document?.IsWorkshared; } catch { }
                if (workshared != false)
                {
                    return CommandResult.Fail(
                        "This machine is configured with force_read_only_on_workshared=true and the active document " +
                        "collaboration state does not allow writes. Run scio_recipe with dry_run=true or disable this policy locally. Nothing ran.");
                }
            }

            ICommand cmd = _resolve(targetToolName);
            if (cmd == null)
            {
                return CommandResult.Fail($"Recipe '{recipeName}' is not registered in the add-in. Target was '{targetToolName}'.");
            }

            return cmd.Execute(app, forwardedArgs.ToString(Formatting.None));
        }
    }
}

