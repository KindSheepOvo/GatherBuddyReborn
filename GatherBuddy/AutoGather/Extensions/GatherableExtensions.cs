using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;

namespace GatherBuddy.AutoGather.Extensions;

/// <summary>
/// Extension methods for the IGatherable interface.
/// </summary>
public static class GatherableExtensions
{
    private static readonly uint[] _retainerInventoryTypes =
        [
            (uint)InventoryType.RetainerCrystals,
            (uint)InventoryType.RetainerPage1,
            (uint)InventoryType.RetainerPage2,
            (uint)InventoryType.RetainerPage3,
            (uint)InventoryType.RetainerPage4,
            (uint)InventoryType.RetainerPage5,
            (uint)InventoryType.RetainerPage6,
            (uint)InventoryType.RetainerPage7
        ];

    /// <summary>
    /// Gets the inventory count for a gatherable item.
    /// </summary>
    /// <param name="gatherable">The gatherable item to check.</param>
    /// <param name="checkRetainers">Check retainer inventory.</param>
    /// <returns>The count of the item in the inventory.</returns>
    public static unsafe int GetInventoryCount(this IGatherable gatherable)
    {
        var inventory = InventoryManager.Instance();
        if (IsCollectableFish(gatherable))
        {
            return inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 1);
        }

        var count = inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 0);

        if (gatherable.ItemData.IsCollectable)
        {
            count += inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 1);
        }

        return count;
    }

    public static int GetTotalCount(this IGatherable gatherable)
        => gatherable.GetTotalCount(true);

    public static int GetTotalCount(this IGatherable gatherable, bool useRetainerInventory)
    {
        var localCount = gatherable.GetInventoryCount();
        if (!useRetainerInventory || IsCollectableFish(gatherable) || !GatherBuddy.Config.AutoGatherConfig.CheckRetainers || !AllaganTools.Enabled)
        {
            return localCount;
        }

        try
        {
            if (!AllaganTools.IsInitialized())
            {
                return localCount;
            }

            var retainerCount = AllaganTools.ItemCountOwned(gatherable.ItemId, true, _retainerInventoryTypes);
            return (int)Math.Min((long)localCount + retainerCount, int.MaxValue);
        }
        catch
        {
            return localCount;
        }
    }

    private static bool IsCollectableFish(IGatherable gatherable)
        => gatherable is Fish && gatherable.ItemData.IsCollectable;
}
