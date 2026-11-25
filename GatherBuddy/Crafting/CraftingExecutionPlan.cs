using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace GatherBuddy.Crafting;

public sealed class CraftingExecutionPlan
{
    private static int _lastVersion;
    private readonly CraftingListDefinition _planningSnapshot;
    private readonly bool _useRetainerCraftableAvailability;
    private readonly CraftingListPlannerOptions _plannerOptions;
    private List<CraftingListItem>? _queue;
    private List<CraftingListItem>? _groupedQueue;

    public int ListId { get; }
    public string ListName { get; }
    public int Version { get; private set; }
    public bool SkipIfEnough { get; }
    public bool SkipFinalIfEnough { get; }
    public bool RetainerRestock { get; }
    public CraftingListPlan ResolvedPlan { get; private set; }

    internal List<CraftingListItem> GroupedQueue => _groupedQueue ??= CraftingListQueueBuilder.CreateGroupedQueue(ResolvedPlan);
    internal List<CraftingListItem> Queue => _queue ??= CraftingListQueueBuilder.CreateExpandedQueue(_planningSnapshot, GroupedQueue);
    internal List<CraftingListItem> OriginalRecipes => ResolvedPlan.OriginalRecipes;
    internal Dictionary<uint, int> Materials => ResolvedPlan.Materials;
    internal Dictionary<uint, int> Precrafts => ResolvedPlan.Precrafts;
    internal Dictionary<uint, int> RetainerConsumedCraftables => ResolvedPlan.RetainerConsumedCraftables;
    internal Dictionary<uint, IngredientQualityDemand> IngredientDemands => ResolvedPlan.IngredientDemands;
    internal CraftingListDefinition PlanningSnapshot => _planningSnapshot;
    internal VulcanRetainerInventory? RetainerInventory => _plannerOptions.RetainerInventory;

    public IReadOnlyList<CraftingListItem> QueueView => Queue;
    public IReadOnlyList<CraftingListItem> OriginalRecipesView => OriginalRecipes;
    public IReadOnlyDictionary<uint, int> MaterialsView => Materials;
    public IReadOnlyDictionary<uint, int> PrecraftsView => Precrafts;
    public IReadOnlyDictionary<uint, int> RetainerConsumedCraftablesView => RetainerConsumedCraftables;
    public IReadOnlyDictionary<uint, IngredientQualityDemand> IngredientDemandsView => IngredientDemands;

    private CraftingExecutionPlan(
        CraftingListDefinition planningSnapshot,
        CraftingListPlannerOptions options,
        CraftingListPlan resolvedPlan)
    {
        _planningSnapshot = planningSnapshot;
        _plannerOptions = options;
        _useRetainerCraftableAvailability = options.UseRetainerCraftableAvailability;
        ListId = planningSnapshot.ID;
        ListName = planningSnapshot.Name;
        SkipIfEnough = planningSnapshot.SkipIfEnough;
        SkipFinalIfEnough = planningSnapshot.SkipFinalIfEnough;
        RetainerRestock = planningSnapshot.RetainerRestock;
        ApplyResolvedPlan(resolvedPlan);
    }

    public static CraftingExecutionPlan Create(CraftingListDefinition list)
        => Build(list.CreateRetainerPlanningSnapshot(), null);

    internal static CraftingExecutionPlan CreateForRun(CraftingStockRun run)
        => Build(run.List, run.ItemTargets, run.RetainerInventory);

    private static CraftingExecutionPlan Build(CraftingListDefinition planningSnapshot, IReadOnlyDictionary<uint, int>? itemTargets,
        VulcanRetainerInventory? retainerInventory = null)
    {
        var useRetainerCraftableAvailability = planningSnapshot.SkipIfEnough && planningSnapshot.RetainerRestock;
        var options = new CraftingListPlannerOptions(useRetainerCraftableAvailability, FinalItemTargets: itemTargets,
            CountFinalRetainerStock: itemTargets != null && planningSnapshot.RetainerRestock)
        {
            RetainerInventory = retainerInventory,
        };
        var resolvedPlan = CraftingListPlanner.Build(planningSnapshot, options);
        return new CraftingExecutionPlan(planningSnapshot, options, resolvedPlan);
    }

    public bool MatchesList(int listId)
        => ListId == listId;

    internal void UpdateRemainingRequirements(CraftingExecutionPlan remaining)
    {
        ResolvedPlan = remaining.ResolvedPlan;
        Version = remaining.Version;
    }

    public void RefreshForRetainerWithdrawal()
    {
        if (!_useRetainerCraftableAvailability)
        {
            return;
        }

        ApplyResolvedPlan(CraftingListPlanner.Build(_planningSnapshot, _plannerOptions));
    }

    public void RefreshFromCurrentInventory()
        => ApplyResolvedPlan(CraftingListPlanner.Build(_planningSnapshot, _plannerOptions with { UseRetainerCraftableAvailability = false }));

    internal IngredientQualityDemand GetMaterialShortage(KeyValuePair<uint, int> material)
    {
        var demand = IngredientDemands.GetValueOrDefault(material.Key);
        if (demand.Total == 0)
        {
            demand = IngredientQualityDemand.FromPreferHQ(material.Value);
        }

        var (nq, hq) = CraftingStockRun.GetInventoryCounts(material.Key);
        return demand.ConsumeSplit(nq, hq, out _, out _);
    }

    public Dictionary<uint, IngredientQualityDemand> BuildQualityTargetsForItems(IReadOnlyDictionary<uint, int> requestedItems)
    {
        var targets = requestedItems.Keys.ToDictionary(
            itemId => itemId,
            itemId => IngredientDemands.GetValueOrDefault(itemId));

        foreach (var (itemId, totalNeeded) in requestedItems)
        {
            var target = targets[itemId];
            if (target.Total < totalNeeded)
            {
                targets[itemId] = target.Add(IngredientQualityDemand.FromPreferHQ(totalNeeded - target.Total));
                continue;
            }

            if (target.Total > totalNeeded)
            {
                targets[itemId] = target.ConsumeUnknownQuality(target.Total - totalNeeded, out _);
            }
        }

        return targets;
    }

    [MemberNotNull(nameof(ResolvedPlan))]
    private void ApplyResolvedPlan(CraftingListPlan resolvedPlan)
    {
        Version = Interlocked.Increment(ref _lastVersion);
        ResolvedPlan = resolvedPlan;
        _groupedQueue = null;
        _queue = null;
    }
}
