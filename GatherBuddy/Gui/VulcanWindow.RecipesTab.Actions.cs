using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherBuddy.Crafting;
using Lumina.Excel.Sheets;
using System;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static unsafe void SwitchJobIfNeeded(uint requiredJobId)
    {
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJob == requiredJobId)
        {
            return;
        }

        try
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Error("[VulcanWindow] Failed to get gearset module");
                return;
            }

            if (GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, requiredJobId, out var gearsetIndex))
            {
                gearsetModule->EquipGearset(gearsetIndex);
                GatherBuddy.Log.Information($"[VulcanWindow] 已切换至职业 {requiredJobId} 的装备套装 {gearsetIndex}");
                return;
            }

            GatherBuddy.Log.Warning($"[VulcanWindow] No gearset found for job {requiredJobId}");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[VulcanWindow] Failed to switch job: {ex.Message}");
        }
    }

    private static void StartCraftWithRaphael(Recipe recipe)
    {
        var requiredJob = (uint)(recipe.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

        if (currentJob != requiredJob)
        {
            GatherBuddy.Log.Information($"[VulcanWindow] Job switch needed: {currentJob} -> {requiredJob}");
            SwitchJobIfNeeded(requiredJob);

            var tm = GatherBuddy.AutoGather?.TaskManager;
            if (tm != null)
            {
                tm.DelayNext(3000);
                tm.Enqueue(() =>
                {
                    StartCraftWithRaphaelAfterJobSwitch(recipe);
                    return true;
                }, "StartCraftAfterJobSwitch");
            }
            return;
        }

        StartCraftWithRaphaelAfterJobSwitch(recipe);
    }

    private static void StartCraftWithRaphaelAfterJobSwitch(Recipe recipe)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        var item = new CraftingListItem(recipe.RowId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };
        var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe, null);
        var qualityPolicy = executionContext.QualityPolicy;
        if (settings != null && settings.HasAnySettings())
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Applying recipe browser settings for {recipe.RowId}");
            if (settings.FoodItemId.HasValue)
            {
                GatherBuddy.Log.Debug($"  Food: {settings.FoodItemId.Value}");
            }

            if (settings.MedicineItemId.HasValue)
            {
                GatherBuddy.Log.Debug($"  Medicine: {settings.MedicineItemId.Value}");
            }

            if (settings.ManualItemId.HasValue)
            {
                GatherBuddy.Log.Debug($"  Manual: {settings.ManualItemId.Value}");
            }

            if (settings.SquadronManualItemId.HasValue)
            {
                GatherBuddy.Log.Debug($"  Squadron Manual: {settings.SquadronManualItemId.Value}");
            }

            GatherBuddy.Log.Debug($"  Ingredient prefs: {settings.IngredientPreferences.Count} items, UseAllNQ={settings.UseAllNQ}");
            CraftingGameInterop.SetQualityPolicy(qualityPolicy);

            var allApplied = ConsumableChecker.ApplyConsumables(settings);
            if (!allApplied)
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Consumables applied, waiting 3 seconds before starting craft");
                var taskMgr = GatherBuddy.AutoGather?.TaskManager;
                taskMgr?.DelayNext(3000);
            }
        }
        else
        {
            CraftingGameInterop.SetQualityPolicy(qualityPolicy);
        }

        var selectedMacroId = executionContext.SelectedMacroId;
        CraftingGameInterop.SetSelectedMacro(selectedMacroId);
        CraftingGameInterop.ReloadSolversForCraft(executionContext.EffectiveSolverMode, !executionContext.ForceProgressOnlyUnlockCraft);
        if (!string.IsNullOrEmpty(selectedMacroId))
        {
            GatherBuddy.Log.Information($"[VulcanWindow] Using macro: {selectedMacroId}");
        }

        if (!CraftingContextResolver.UsesRaphaelSolver(executionContext))
        {
            CraftingGameInterop.StartCraft(recipe, 1);
            return;
        }
        var requiredJob = (uint)(recipe.CraftType.RowId + 8);

        if (!CraftingContextResolver.TryBuildSimulationContext(recipe, executionContext, CraftingStatsSource.AlwaysGearsetStats, out var simulationContext))
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Could not read gearset stats for job {requiredJob}, crafting without Raphael");
            CraftingGameInterop.StartCraft(recipe, 1);
            return;
        }
        var request = simulationContext.RaphaelRequest;

        if (GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var solution) && solution != null && !solution.IsFailed)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Raphael solution already cached for recipe {recipe.RowId}, starting craft");
            CraftingGameInterop.StartCraft(recipe, 1);
            return;
        }

        GatherBuddy.Log.Information($"[VulcanWindow] Enqueuing Raphael solve for recipe {recipe.RowId}");
        GatherBuddy.RaphaelSolveCoordinator.EnqueueOrPromoteRequest(request, RaphaelSolvePriority.Urgent);

        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm == null)
        {
            GatherBuddy.Log.Error($"[VulcanWindow] TaskManager unavailable, cannot wait for Raphael solve");
            return;
        }

        tm.Enqueue(() => WaitForRaphaelSolution(request), 60000, "WaitForRaphaelSolution");
        tm.Enqueue(() =>
        {
            GatherBuddy.Log.Information($"[VulcanWindow] Raphael solution ready, starting craft for recipe {recipe.RowId}");
            CraftingGameInterop.StartCraft(recipe, 1);
            return true;
        }, "StartCraftAfterRaphael");
    }

    private static bool WaitForRaphaelSolution(RaphaelSolveRequest request)
    {
        if (GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var solution))
        {
            if (solution != null && !solution.IsFailed)
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Raphael solution ready for recipe {request.RecipeId}");
                return true;
            }
            else if (solution != null && solution.IsFailed)
            {
                GatherBuddy.Log.Warning($"[VulcanWindow] Raphael solution failed for recipe {request.RecipeId}: {solution.FailureReason}");
                return true;
            }
        }

        return false;
    }

    private static bool StartBrowserQuickSynth(Recipe recipe, int quantity)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        return CraftingGatherBridge.StartRecipeBrowserCraft(recipe.RowId, recipe.ItemResult.Value.Name.ExtractText(), quantity,
            quickSynth: true, _browserRetainerRestock, settings);
    }

    private static bool StartBrowserCraft(Recipe recipe, int quantity)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        return CraftingGatherBridge.StartRecipeBrowserCraft(recipe.RowId, recipe.ItemResult.Value.Name.ExtractText(), quantity,
            quickSynth: false, _browserRetainerRestock, settings?.HasAnySettings() == true ? settings : null);
    }

}
