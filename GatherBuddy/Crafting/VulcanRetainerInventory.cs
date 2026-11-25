using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

internal sealed unsafe class VulcanRetainerInventory
{
    internal readonly record struct Retainer(uint SortedIndex, ulong Id);

    internal static ReadOnlySpan<InventoryType> Containers =>
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    private readonly List<Retainer> _retainers = [];
    private readonly Dictionary<ulong, Dictionary<uint, (int NQ, int HQ)>> _stock = [];
    private readonly Dictionary<uint, (int NQ, int HQ)> _totals = [];
    private bool _hasRetainerList;

    public IReadOnlyList<Retainer> Retainers => _retainers;
    public bool IsComplete => _hasRetainerList && _stock.Count == _retainers.Count;

    public bool HasInventory(ulong retainerId) => _stock.ContainsKey(retainerId);

    public (int NQ, int HQ) GetCounts(uint itemId) => _totals.GetValueOrDefault(itemId);

    public (int NQ, int HQ) GetCounts(ulong retainerId, uint itemId)
        => _stock.TryGetValue(retainerId, out var items) ? items.GetValueOrDefault(itemId) : default;

    public int GetTotalCount(uint itemId)
    {
        var (nq, hq) = GetCounts(itemId);
        return nq + hq;
    }

    public void Reset()
    {
        _hasRetainerList = false;
        _retainers.Clear();
        _stock.Clear();
        _totals.Clear();
    }

    public bool ReadRetainerList()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
        {
            return false;
        }

        var count = manager->GetRetainerCount();
        var retainers = new List<Retainer>(count);
        for (uint index = 0; index < count; index++)
        {
            var retainer = manager->GetRetainerBySortedIndex(index);
            if (retainer == null || retainer->RetainerId == 0)
            {
                return false;
            }

            retainers.Add(new Retainer(index, retainer->RetainerId));
        }

        foreach (var id in _stock.Keys.Where(id => !retainers.Exists(retainer => retainer.Id == id)).ToArray())
        {
            Invalidate(id);
        }

        _retainers.Clear();
        _retainers.AddRange(retainers);
        _hasRetainerList = true;
        return true;
    }

    public void ReadCurrentGameSnapshots()
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || finder->CharacterContentId != Dalamud.PlayerState.ContentId)
        {
            return;
        }

        foreach (var retainer in _retainers)
        {
            // The game keeps older item-search data too. Only reuse snapshots loaded from the server this session.
            if (HasInventory(retainer.Id) || !finder->IsRetainerCurrent(retainer.Id)
                || !finder->RetainerInventories.TryGetValue(retainer.Id, out var stored, copyCtor: false) || stored.Value == null)
            {
                continue;
            }

            var items = new Dictionary<uint, (int NQ, int HQ)>();
            var ids = stored.Value->ItemIds;
            var counts = stored.Value->ItemCount;
            for (var slot = 0; slot < ids.Length; slot++)
            {
                var id = ids[slot];
                if (id is >= 500_000 and < 1_000_000)
                {
                    continue;
                }

                var hq = id >= 1_000_000;
                var itemId = hq ? id - 1_000_000 : id;
                AddCount(items, itemId, counts[slot], hq);
            }

            var crystals = stored.Value->CrystalQuantities;
            for (var slot = 0; slot < crystals.Length; slot++)
            {
                AddCount(items, (uint)slot + 2, crystals[slot], hq: false);
            }

            Record(retainer.Id, items);
        }
    }

    public bool TryReadOpenedRetainer(ulong retainerId)
    {
        var inventory = GetOpenedInventory(retainerId, requireAllPages: true);
        if (inventory == null)
        {
            return false;
        }

        var items = new Dictionary<uint, (int NQ, int HQ)>();
        foreach (var type in Containers)
        {
            var container = inventory->GetInventoryContainer(type);
            for (var index = 0; index < container->Size; index++)
            {
                var slot = container->GetInventorySlot(index);
                if (slot != null && (slot->Flags & InventoryItem.ItemFlags.Collectable) == 0)
                {
                    AddCount(items, slot->ItemId, (int)slot->Quantity, (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0);
                }
            }
        }

        Record(retainerId, items);
        return true;
    }

    internal static InventoryManager* GetOpenedInventory(ulong retainerId, bool requireAllPages = false)
    {
        if (!Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            return null;
        }

        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.Retainer);
        var inventory = InventoryManager.Instance();
        if (active == null || active->RetainerId != retainerId || agent == null || !agent->IsAgentActive() || inventory == null)
        {
            return null;
        }

        if (requireAllPages)
        {
            foreach (var type in Containers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                {
                    return null;
                }
            }
        }

        return inventory;
    }

    public void Invalidate(ulong retainerId)
    {
        if (!_stock.Remove(retainerId, out var previous))
        {
            return;
        }

        foreach (var (itemId, (nq, hq)) in previous)
        {
            var total = _totals[itemId];
            if (total == (nq, hq))
            {
                _totals.Remove(itemId);
            }
            else
            {
                _totals[itemId] = (total.NQ - nq, total.HQ - hq);
            }
        }
    }

    private void Record(ulong retainerId, Dictionary<uint, (int NQ, int HQ)> items)
    {
        Invalidate(retainerId);
        _stock[retainerId] = items;
        foreach (var (itemId, (nq, hq)) in items)
        {
            var total = _totals.GetValueOrDefault(itemId);
            _totals[itemId] = (total.NQ + nq, total.HQ + hq);
        }
    }

    private static void AddCount(Dictionary<uint, (int NQ, int HQ)> items, uint itemId, int count, bool hq)
    {
        if (itemId == 0 || count <= 0)
        {
            return;
        }

        var previous = items.GetValueOrDefault(itemId);
        items[itemId] = hq ? (previous.NQ, previous.HQ + count) : (previous.NQ + count, previous.HQ);
    }
}
