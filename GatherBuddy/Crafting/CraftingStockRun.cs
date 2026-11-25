using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

internal sealed class CraftingStockRun
{
    private bool _turnInProgressNeedsSave;

    public ulong CharacterId { get; init; }
    public CraftingListDefinition List { get; init; } = new();
    public Dictionary<uint, int> ItemTargets { get; init; } = [];
    public bool AllowCollectableTurnIn { get; init; }
    public bool Paused { get; set; }
    public VulcanRetainerInventory? RetainerInventory { get; init; }
    public bool NeedsAdditionalBaseline { get; private set; }
    public bool IsRecipeBrowserOrder { get; init; }
    public RecipeBrowserOrder? BrowserOrder { get; set; }

    public static CraftingStockRun Create(CraftingListDefinition list, ulong characterId, bool additionalCrafts, bool recipeBrowser = false)
    {
        var snapshot = list.CreateRetainerPlanningSnapshot();
        snapshot.SkipIfEnough = true;
        snapshot.SkipFinalIfEnough = true;
        var targets = new Dictionary<uint, int>();
        foreach (var item in snapshot.Recipes)
        {
            if (item.Options.Skipping || item.Quantity <= 0 || RecipeManager.GetRecipe(item.RecipeId) is not { } recipe)
            {
                continue;
            }

            var count = (long)item.Quantity * recipe.AmountResult;
            if (additionalCrafts && !snapshot.RetainerRestock)
            {
                count += GetInventoryCount(recipe.ItemResult.RowId);
            }

            targets[item.RecipeId] = (int)Math.Min(count, int.MaxValue);
        }

        return new CraftingStockRun
        {
            CharacterId = characterId,
            List = snapshot,
            ItemTargets = targets,
            AllowCollectableTurnIn = !additionalCrafts,
            RetainerInventory = snapshot.RetainerRestock ? new VulcanRetainerInventory() : null,
            NeedsAdditionalBaseline = additionalCrafts && snapshot.RetainerRestock,
            IsRecipeBrowserOrder = recipeBrowser,
        };
    }

    public static CraftingStockRun Restore(RecipeBrowserOrder order)
        => new()
        {
            CharacterId = order.CharacterId,
            List = order.CreateList(),
            ItemTargets = new Dictionary<uint, int> { [order.RecipeId] = order.TargetCount },
            RetainerInventory = order.RetainerRestock ? new VulcanRetainerInventory() : null,
            IsRecipeBrowserOrder = true,
            BrowserOrder = order,
        };

    public RecipeBrowserOrder CreateBrowserOrder(uint itemId)
    {
        var item = List.Recipes[0];
        return new RecipeBrowserOrder(Guid.NewGuid(), CharacterId, item.RecipeId, itemId, List.Name,
            item.Quantity, ItemTargets[item.RecipeId], item.Options.NQOnly, List.RetainerRestock, item.CraftSettings);
    }

    public void SetAdditionalBaseline()
    {
        if (!NeedsAdditionalBaseline || RetainerInventory is not { IsComplete: true } retainers)
        {
            return;
        }

        foreach (var (recipeId, target) in ItemTargets)
        {
            if (RecipeManager.GetRecipe(recipeId) is { } recipe)
            {
                var itemId = recipe.ItemResult.RowId;
                ItemTargets[recipeId] = (int)Math.Min((long)target + GetInventoryCount(itemId) + retainers.GetTotalCount(itemId), int.MaxValue);
            }
        }

        NeedsAdditionalBaseline = false;
    }

    public void RecordTurnedInCollectable(uint itemId)
    {
        foreach (var (recipeId, target) in ItemTargets)
        {
            if (target <= 0 || RecipeManager.GetRecipe(recipeId) is not { AmountResult: 1 } recipe
                || recipe.ItemResult.RowId != itemId || !recipe.ItemResult.Value.AlwaysCollectable)
            {
                continue;
            }

            ItemTargets[recipeId] = target - 1;
            var plannedItem = List.Recipes.Find(item => item.RecipeId == recipeId);
            if (plannedItem != null)
            {
                plannedItem.Quantity = target - 1;
            }

            var savedItem = GatherBuddy.CraftingListManager.GetListByID(List.ID)?.Recipes.Find(item => item.RecipeId == recipeId);
            if (savedItem is { Quantity: > 0 })
            {
                savedItem.Quantity--;
                _turnInProgressNeedsSave = true;
            }

            return;
        }
    }

    public bool TrySaveTurnInProgress()
    {
        if (_turnInProgressNeedsSave && !GatherBuddy.CraftingListManager.SaveTurnInProgress())
        {
            return false;
        }

        _turnInProgressNeedsSave = false;
        return true;
    }

    internal static int GetInventoryCount(uint itemId)
    {
        var (nq, hq) = GetInventoryCounts(itemId);
        return nq + hq;
    }

    internal static unsafe (int NQ, int HQ) GetInventoryCounts(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory == null
            ? (0, 0)
            : (inventory->GetInventoryItemCount(itemId, false, false, false),
                inventory->GetInventoryItemCount(itemId, true, false, false));
    }

    internal static unsafe bool IsInventoryReady()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            return false;
        }

        foreach (var type in global::GatherBuddy.AutoGather.AutoGather.InventoryTypes)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                return false;
            }
        }

        return true;
    }

    internal static unsafe bool IsInventoryFull()
    {
        var inventory = InventoryManager.Instance();
        return inventory != null && inventory->GetEmptySlotsInBag() == 0;
    }
}
