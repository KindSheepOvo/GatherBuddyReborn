using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GatherBuddy.Crafting;

public static partial class CraftingGatherBridge
{
    private static CraftingStockRun? _stockRun;
    private static bool _stockPlanDirty;
    private static bool _collectablesCheckPending;
    private static readonly TimeSpan InventorySettleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReadinessRetryDelay = TimeSpan.FromMilliseconds(750);
    private static RetainerTaskExecutor? _retainerVerification;
    private static bool _refreshRetainerInventory;
    private static CancellationTokenSource? _stockRunCancellation;
    private static bool _stockWorkScheduled;
    private static bool _gatherStopped;
    private static readonly HashSet<uint> StockTargetItems = [];
    private static Dictionary<uint, int> _outputsBeforePass = [];

    internal static string? StockStatus { get; private set; }
    internal static bool StockRunPaused => _stockRun?.Paused == true;
    internal static bool IsRecipeBrowserStockRun => _stockRun?.IsRecipeBrowserOrder == true;
    internal static bool IsGatheringForStockRun => _stockRun != null && _gatherList != null;

    internal static bool HoldQueueForStockPlan(CraftingQueueProcessor processor)
        => _stockRun != null && (_collectablesStartPending || _waitingForCollectables || _waitingForCollectablesHomeReturn
            || processor.CanRefreshStockPlan && (_stockPlanDirty || _collectablesCheckPending));

    private static void SetStockStatus(string? status)
    {
        if (status == StockStatus)
        {
            return;
        }

        var open = StockStatus == null && status != null;
        StockStatus = status;
        if (open)
        {
            GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
        }
    }

    private static void OnGameLogout(int type, int code)
        => ReleaseStockRuntime();

    private static void ActivateStockRun(CraftingStockRun run)
    {
        _stockRun = run;
        _nextCollectablesRetry = DateTime.MinValue;
        _stockRunCancellation = new CancellationTokenSource();
        foreach (var recipeId in run.ItemTargets.Keys)
        {
            if (RecipeManager.GetRecipe(recipeId) is { } recipe)
            {
                StockTargetItems.Add(recipe.ItemResult.RowId);
            }
        }

        Dalamud.ClientState.Logout += OnGameLogout;
        Dalamud.GameInventory.InventoryChanged += OnInventoryChanged;
        Dalamud.Conditions.ConditionChange += OnStockConditionChanged;
        CraftingGameInterop.StateChanged += OnCraftStateChanged;
        GatherBuddy.AutoGather.EnabledChanged += OnGatherEnabledChanged;
        GatherBuddy.CollectableManager.VulcanTurnInStopped += OnStockTurnInStopped;
    }

    public static void StartQueueCraftAndGather(CraftingListDefinition list, bool additionalCrafts = false)
        => TryStartStockRun(list, additionalCrafts);

    internal static bool StartRecipeBrowserCraft(uint recipeId, string name, int quantity, bool quickSynth,
        bool retainerRestock, RecipeCraftSettings? settings)
        => TryStartStockRun(RecipeBrowserOrder.CreateList(recipeId, name, quantity, quickSynth, retainerRestock, settings),
            additionalCrafts: true, recipeBrowser: true);

    internal static bool ResumeRecipeBrowserCraft(RecipeBrowserOrder order)
        => TryStartStockRun(order.CreateList(), additionalCrafts: true, recipeBrowser: true, expectedOrder: order);

    internal static RecipeBrowserOrderState GetPendingRecipeBrowserOrder(bool refresh = false)
    {
        var characterId = GetStockCharacterId();
        return characterId != 0 && _stockRun == null && _queueProcessor == null
            ? RecipeBrowserOrderStore.Get(characterId, refresh)
            : default;
    }

    internal static void DiscardPendingRecipeBrowserOrder(RecipeBrowserOrderState pending)
    {
        var characterId = GetStockCharacterId();
        if (characterId == 0 || characterId != pending.CharacterId || _stockRun != null || _queueProcessor != null)
        {
            return;
        }

        if (!RecipeBrowserOrderStore.TryRemove(characterId, pending.Order?.Id))
        {
            Communicator.Print("[Vulcan] 无法放弃上次制作。请检查恢复记录是否已更改或插件目录权限。");
        }
    }

    private static ulong GetStockCharacterId()
        => Dalamud.ClientState.IsLoggedIn && Dalamud.PlayerState.IsLoaded ? Dalamud.PlayerState.ContentId : 0;

    private static bool TryStartStockRun(CraftingListDefinition list, bool additionalCrafts, bool recipeBrowser = false,
        RecipeBrowserOrder? expectedOrder = null)
    {
        var characterId = GetStockCharacterId();
        if (characterId == 0)
        {
            Communicator.Print("[Vulcan] 等待角色背包加载后再开始制作。");
            return false;
        }

        if (_stockRun is { } activeRun && activeRun.CharacterId == characterId
            && (list.ID >= 0 && list.ID == activeRun.List.ID
                || recipeBrowser && activeRun.IsRecipeBrowserOrder && list.Recipes[0].RecipeId == activeRun.List.Recipes[0].RecipeId
                    && (expectedOrder == null || activeRun.BrowserOrder?.Id == expectedOrder.Id)))
        {
            ReplanStockRun(activeRun);
            GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
            return true;
        }

        if (_stockRun != null || _queueProcessor != null)
        {
            GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
            Communicator.Print("[Vulcan] 已有制作清单正在执行或等待继续。");
            return false;
        }

        if (recipeBrowser)
        {
            var pending = RecipeBrowserOrderStore.Get(characterId, refresh: true);
            if (pending.ReadFailed)
            {
                Communicator.Print("[Vulcan] 无法读取上次制作的恢复记录。请在配方页处理该记录。");
                return false;
            }

            if (expectedOrder != null && (expectedOrder.CharacterId != characterId || pending.Order?.Id != expectedOrder.Id))
            {
                Communicator.Print("[Vulcan] 上次制作的恢复记录已变更。");
                return false;
            }

            if (pending.Order is { } order)
            {
                if (list.Recipes[0].RecipeId != order.RecipeId)
                {
                    Communicator.Print("[Vulcan] 此角色还有未完成的配方制作。请先继续或放弃上次的任务。");
                    return false;
                }

                if (RecipeManager.GetRecipe(order.RecipeId) is not { AmountResult: > 0 } recipe
                    || recipe.ItemResult.RowId != order.ItemId || order.QuickSynth && !recipe.CanQuickSynth)
                {
                    Communicator.Print("[Vulcan] 上次配方或制作方式已变更。请放弃该任务后重新选择配方。");
                    return false;
                }

                var restored = CraftingStockRun.Restore(order);
                ActivateStockRun(restored);
                ReplanStockRun(restored);
                return true;
            }
        }

        if (additionalCrafts && !CraftingStockRun.IsInventoryReady())
        {
            Communicator.Print("[Vulcan] 等待角色背包加载后再开始制作。");
            return false;
        }

        var run = CraftingStockRun.Create(list, characterId, additionalCrafts, recipeBrowser);
        if (run.ItemTargets.Count == 0)
        {
            return false;
        }

        ActivateStockRun(run);
        ReplanStockRun(run);
        return true;
    }

    private static void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (_stockRun is not { Paused: false } run || _waitingForCollectables || _waitingForCollectablesHomeReturn)
        {
            return;
        }

        var waitingForGather = _queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.WaitingForGather;
        var gathering = waitingForGather && GatherBuddy.AutoGather.Enabled;
        var checkTurnInOnBagChange = run.AllowCollectableTurnIn && GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables;
        var checkCollectables = false;
        var checkPlan = false;
        foreach (var change in events)
        {
            if (change is InventoryComplexEventArgs move)
            {
                var source = move.SourceEvent.Item;
                var destination = move.TargetEvent.Item;
                var sourceInBag = IsBagInventory(source.ContainerType);
                var destinationInBag = IsBagInventory(destination.ContainerType);
                var bothInBag = sourceInBag && destinationInBag;
                if (bothInBag && change is InventoryItemMovedArgs)
                {
                    continue;
                }

                if (!bothInBag && source.ContainerType != destination.ContainerType)
                {
                    checkPlan |= IsRelevantChange(source, gathering) || IsRelevantChange(destination, gathering);
                }
                checkCollectables |= checkTurnInOnBagChange && (sourceInBag || destinationInBag);
                continue;
            }

            if (change is InventoryItemChangedArgs changed)
            {
                var oldItem = changed.OldItemState;
                var item = changed.Item;
                if (oldItem.BaseItemId == item.BaseItemId && oldItem.Quantity == item.Quantity && oldItem.IsHq == item.IsHq
                    && oldItem.IsCollectable == item.IsCollectable)
                {
                    continue;
                }

                checkPlan |= IsRelevantChange(oldItem, gathering);
                checkCollectables |= checkTurnInOnBagChange && IsBagInventory(item.ContainerType) && (oldItem.IsCollectable || item.IsCollectable);
            }
            else if (checkTurnInOnBagChange && IsBagInventory(change.Item.ContainerType))
            {
                checkCollectables = true;
            }

            checkPlan |= IsRelevantChange(change.Item, gathering);
        }

        if (checkPlan)
        {
            _stockPlanDirty = true;
            _gatherStopped |= waitingForGather && !gathering;
        }

        if (checkPlan || checkCollectables)
        {
            _collectablesCheckPending |= checkCollectables;
            ScheduleStockWork(InventorySettleDelay);
        }
    }

    private static bool IsRelevantChange(GameInventoryItem item, bool gathering)
    {
        var id = item.BaseItemId;
        return id != 0 && (StockTargetItems.Contains(id) || _activeExecutionPlan?.Precrafts.ContainsKey(id) == true
            || !gathering && _activeExecutionPlan?.Materials.ContainsKey(id) == true);
    }

    private static bool IsBagInventory(GameInventoryType inventory)
        => inventory is >= GameInventoryType.Inventory1 and <= GameInventoryType.Inventory4;

    private static void MarkStockPlanDirty()
    {
        if (_stockRun != null)
        {
            _stockPlanDirty = true;
            ScheduleStockWork(InventorySettleDelay);
        }
    }

    private static void OnGatherEnabledChanged(bool enabled)
    {
        if (!enabled && _stockRun != null && _waitingForGatherComplete && !PreserveListOnDisable
            && _queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.WaitingForGather)
        {
            _gatherStopped = true;
            ScheduleStockWork(InventorySettleDelay);
        }
    }

    private static void OnStockQueueStateChanged(CraftingQueueProcessor.QueueState state)
    {
        if (state == CraftingQueueProcessor.QueueState.Complete)
        {
            _collectablesCheckPending |= _stockRun?.AllowCollectableTurnIn == true;
            MarkStockPlanDirty();
        }
        else
        {
            OnStockCheckpoint();
        }
    }

    private static void OnStockCheckpoint()
    {
        if (_stockRun is not { Paused: false } run)
        {
            return;
        }

        if (_queueProcessor?.PausedForInventoryFull == true)
        {
            _stockPlanDirty = true;
            _collectablesCheckPending |= run.AllowCollectableTurnIn;
        }

        SchedulePendingStockWork();
    }

    private static void OnStockConditionChanged(ConditionFlag flag, bool enabled)
    {
        if (!enabled && flag == ConditionFlag.OccupiedSummoningBell
            && _stockRun is { Paused: false, RetainerInventory: not null } && _retainerVerification == null
            && _queueProcessor?.CurrentState != CraftingQueueProcessor.QueueState.WithdrawingFromRetainer)
        {
            _refreshRetainerInventory = true;
            MarkStockPlanDirty();
            return;
        }

        if (!enabled && flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51
            or ConditionFlag.Crafting or ConditionFlag.ExecutingCraftingAction or ConditionFlag.PreparingToCraft
            or ConditionFlag.OccupiedInQuestEvent or ConditionFlag.OccupiedSummoningBell)
        {
            SchedulePendingStockWork();
        }
    }

    private static void OnCraftStateChanged(CraftingGameInterop.CraftState state)
    {
        if (state is CraftingGameInterop.CraftState.IdleNormal or CraftingGameInterop.CraftState.IdleBetween)
        {
            SchedulePendingStockWork();
        }
    }

    private static void SchedulePendingStockWork()
    {
        if (_stockRun is { Paused: false } && (_stockPlanDirty || _collectablesCheckPending || _collectablesStartPending))
        {
            ScheduleStockWork(InventorySettleDelay);
        }
    }

    private static void ScheduleStockWork(TimeSpan delay)
    {
        if (_stockWorkScheduled || _stockRunCancellation is not { } cancellation)
        {
            return;
        }

        _stockWorkScheduled = true;
        _ = Dalamud.Framework.RunOnTick(() =>
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            _stockWorkScheduled = false;
            try
            {
                ProcessStockWork();
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"[Vulcan] 更新制作清单失败: {ex}");
                _retainerVerification?.StopNavigation();
                _retainerVerification = null;
                PauseStockRun("更新制作清单失败。");
            }
        }, delay: delay, cancellationToken: cancellation.Token);
    }

    private static void ProcessStockWork()
    {
        if (_stockRun is not { } run)
        {
            return;
        }

        var characterLoaded = Dalamud.PlayerState.IsLoaded;
        if (!Dalamud.ClientState.IsLoggedIn || characterLoaded && Dalamud.PlayerState.ContentId != run.CharacterId)
        {
            ReleaseStockRuntime();
            return;
        }

        if (run.IsRecipeBrowserOrder && !characterLoaded)
        {
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        if (_retainerVerification is { } verification)
        {
            if (verification.Tick() == CraftingTasks.TaskResult.Retry)
            {
                ScheduleStockWork(verification.NextCheckDelay);
                return;
            }

            _retainerVerification = null;
            if (!run.Paused && !verification.CloseRequested && (!verification.IsComplete || run.RetainerInventory?.IsComplete != true))
            {
                PauseStockRun(verification.FailureReason ?? "未能核实雇员库存。请结束当前雇员操作后点击「继续」。");
                return;
            }
        }

        if (run.Paused || _waitingForCollectables)
        {
            return;
        }

        if (_waitingForCollectablesHomeReturn)
        {
            ContinueCollectablesHomeReturn(run);
            return;
        }

        if (_collectablesStartPending)
        {
            ContinueStockTurnIn(run);
            return;
        }

        if (_gatherStopped)
        {
            _gatherStopped = false;
            if (!GatherBuddy.AutoGather.Enabled && _queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.WaitingForGather)
            {
                if (CraftingStockRun.IsInventoryFull())
                {
                    _stockPlanDirty = true;
                    _collectablesCheckPending |= run.AllowCollectableTurnIn;
                }
                else if (_gatherList != null && IsGatheringComplete())
                {
                    _stockPlanDirty |= _activeExecutionPlan?.Materials.Any(NeedsGathering) == true;
                    OnGatherComplete();
                }
                else
                {
                    StopTemporaryGathering();
                    PauseStockRun("采集已停止，清单仍有材料缺口。点击「继续」按现有库存重新计算。");
                    return;
                }
            }
        }

        var pausedForInventory = _queueProcessor?.PausedForInventoryFull == true;
        if (_queueProcessor is { CanRefreshStockPlan: false } && !pausedForInventory)
        {
            return;
        }

        if (!_stockPlanDirty && !_collectablesCheckPending)
        {
            return;
        }

        if (CraftingGameInterop.CurrentState is not (CraftingGameInterop.CraftState.IdleNormal or CraftingGameInterop.CraftState.IdleBetween)
            || Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            return;
        }

        if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            SetStockStatus("等待雇员操作结束后重新计算制作清单");
            return;
        }

        if (!GenericHelpers.IsScreenReady() || !CraftingStockRun.IsInventoryReady())
        {
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        run.AccountForTurnedInCollectables();
        if (TryBeginStockTurnIn(run) || !_stockPlanDirty)
        {
            return;
        }

        var retainers = run.RetainerInventory;
        if (_refreshRetainerInventory && retainers != null)
        {
            _refreshRetainerInventory = false;
            retainers.Reset();
            if (retainers.ReadRetainerList())
            {
                retainers.ReadCurrentGameSnapshots();
            }
        }

        if (run.NeedsAdditionalBaseline && retainers is { IsComplete: false })
        {
            StartRetainerVerification(retainers);
            return;
        }

        run.SetAdditionalBaseline();
        if (run.IsRecipeBrowserOrder && run.BrowserOrder == null)
        {
            var recipe = RecipeManager.GetRecipe(run.List.Recipes[0].RecipeId);
            if (recipe is not { AmountResult: > 0 } currentRecipe)
            {
                PauseStockRun("无法读取配方产物，追加制作已暂停。");
                return;
            }

            var order = run.CreateBrowserOrder(currentRecipe.ItemResult.RowId);
            if (!RecipeBrowserOrderStore.TryCreate(order))
            {
                PauseStockRun("无法保存追加制作目标。请检查插件目录权限，或停止当前任务后继续已保存的任务。");
                return;
            }

            run.BrowserOrder = order;
        }

        _stockPlanDirty = false;
        var next = CraftingExecutionPlan.CreateForRun(run);
        if (next.ResolvedPlan.Recipes.Count == 0 && next.RetainerConsumedCraftables.Count == 0)
        {
            FinishStockRun();
            return;
        }

        if (pausedForInventory)
        {
            SetStockStatus(null);
            return;
        }

        if (_queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.WaitingForGather
            && !GatherBuddy.AutoGather.Enabled && CraftingStockRun.IsInventoryFull())
        {
            PauseStockRun("背包已满，腾出空间后点击「继续」。");
            return;
        }

        if (retainers is { IsComplete: false })
        {
            StartRetainerVerification(retainers);
            return;
        }

        if (_queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.Complete
         && _outputsBeforePass.Count > 0 && _outputsBeforePass.All(item => CraftingStockRun.GetInventoryCount(item.Key) <= item.Value))
        {
            PauseStockRun("这批操作结束后仍有制作缺口，背包中的产物数量没有增加。");
            return;
        }

        SetStockStatus(null);
        if (_queueProcessor is { CurrentState: not (CraftingQueueProcessor.QueueState.Idle or CraftingQueueProcessor.QueueState.Complete) } current
            && current.MatchesRemainingPlan(next, compareGatherTargets: _gatherList != null)
            && (_gatherList != null || !next.Materials.Any(NeedsGathering)))
        {
            current.UpdateRemainingRequirements(next);
            return;
        }

        _outputsBeforePass = next.ResolvedPlan.Recipes.Select(item => RecipeManager.GetRecipe(item.RecipeId)?.ItemResult.RowId ?? 0)
            .Where(itemId => itemId != 0).Distinct().ToDictionary(itemId => itemId, CraftingStockRun.GetInventoryCount);
        StartQueue(next, run.List.Consumables, run.List.Ephemeral ? run.List.ID : null);
    }

    private static void StartRetainerVerification(VulcanRetainerInventory retainers)
    {
        _stockPlanDirty = true;

        DiscardStockQueue();
        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null, restoreWindow: false);
        SetStockStatus("正在从游戏核实雇员库存");
        TryExitCraftingUiForCollectables();
        if (!IsReadyToStartCollectables(out _))
        {
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        _retainerVerification = new RetainerTaskExecutor(retainers);
        ScheduleStockWork(InventorySettleDelay);
    }

    private static bool NeedsGathering(KeyValuePair<uint, int> material)
    {
        var target = GetCraftingGatherTargetQuantity(material.Key, material.Value, out var itemId);
        return target > CraftingStockRun.GetInventoryCount(itemId)
            && (GatherBuddy.GameData.Gatherables.ContainsKey(itemId) || GatherBuddy.GameData.Fishes.ContainsKey(itemId));
    }

    private static void StopTemporaryGathering()
    {
        if (_gatherList != null)
        {
            _waitingForGatherComplete = false;
            GatherBuddy.AutoGather.Enabled = false;
            DeleteTemporaryGatherList();
        }
    }

    internal static void PauseStockRun(string status)
    {
        if (_stockRun != null)
        {
            _stockRun.Paused = true;
        }

        if (_retainerVerification is { } verification)
        {
            verification.StopNavigation();
            if (verification.RequestClose())
            {
                ScheduleStockWork(verification.NextCheckDelay);
            }
            else
            {
                _retainerVerification = null;
            }
        }

        _queueProcessor?.Pause(status);
        SetStockStatus(status);
    }

    private static void ReplanStockRun(CraftingStockRun run)
    {
        var discardPausedQueue = run.Paused || _queueProcessor?.Paused == true;
        run.Paused = false;
        _refreshRetainerInventory = run.RetainerInventory != null && _retainerVerification is not { CloseRequested: false };
        SetStockStatus($"正在按当前库存计算 {run.List.Name} 的剩余目标");
        if (discardPausedQueue && _queueProcessor?.CloseRetainerForReplan() != true)
        {
            DiscardStockQueue();
            GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null, restoreWindow: false);
        }

        if (!_waitingForCollectables && !_waitingForCollectablesHomeReturn)
        {
            ResetCollectablesInterruptionState();
        }

        _collectablesCheckPending = run.AllowCollectableTurnIn;
        MarkStockPlanDirty();
    }

    internal static void SetUserPaused(bool paused)
    {
        if (_stockRun is { } run)
        {
            if (paused)
            {
                PauseStockRun("制作清单已暂停");
            }
            else
            {
                ReplanStockRun(run);
            }

            return;
        }

        if (paused)
        {
            _queueProcessor?.Pause();
        }
        else
        {
            _queueProcessor?.Resume();
        }
    }

    private static void FinishStockRun(bool completed = true)
    {
        if (_stockRun is not { } run)
        {
            return;
        }

        ReleaseStockRuntime();
        if (run.BrowserOrder is { } order && !RecipeBrowserOrderStore.TryRemove(order.CharacterId, order.Id))
        {
            Communicator.Print("[Vulcan] 无法清除追加制作的恢复记录。请在配方页检查该任务。");
        }

        if (completed)
        {
            GatherBuddy.Log.Information($"[Vulcan] 制作清单「{run.List.Name}」的目标已完成。");
            if (run.List.Ephemeral && run.List.ID > 0)
            {
                GatherBuddy.CraftingListManager.DeleteList(run.List.ID);
            }
        }
    }

    private static void DiscardStockQueue()
    {
        StopTemporaryGathering();
        _queueProcessor?.Reset();
        _queueProcessor = null;
        _activeExecutionPlan = null;
        _waitingForGatherComplete = false;
        _gatherStopped = false;
    }

    private static void ReleaseStockRuntime()
    {
        if (_stockRun == null)
        {
            return;
        }

        _stockRun = null;
        _stockRunCancellation?.Cancel();
        _stockRunCancellation?.Dispose();
        _stockRunCancellation = null;
        _stockWorkScheduled = false;
        Dalamud.ClientState.Logout -= OnGameLogout;
        Dalamud.GameInventory.InventoryChanged -= OnInventoryChanged;
        Dalamud.Conditions.ConditionChange -= OnStockConditionChanged;
        CraftingGameInterop.StateChanged -= OnCraftStateChanged;
        GatherBuddy.AutoGather.EnabledChanged -= OnGatherEnabledChanged;
        GatherBuddy.CollectableManager.VulcanTurnInStopped -= OnStockTurnInStopped;
        if (GatherBuddy.CollectableManager is { IsRunning: true, CurrentRunSource: CollectableRunSource.VulcanQueue })
        {
            GatherBuddy.CollectableManager.Stop();
        }

        if (_isQueueMode)
        {
            CraftingGameInterop.CancelCurrentCraft();
        }

        DiscardStockQueue();
        _isQueueMode = false;
        _ephemeralListId = null;
        _stockPlanDirty = false;
        _collectablesCheckPending = false;
        _retainerVerification?.StopNavigation();
        _retainerVerification = null;
        _refreshRetainerInventory = false;
        StockTargetItems.Clear();
        _outputsBeforePass.Clear();
        ResetCollectablesInterruptionState();
        SetStockStatus(null);
        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
    }

    internal static void Dispose()
    {
        ReleaseStockRuntime();
        RecipeBrowserOrderStore.ClearCache();
        VulcanRetainerPreview.Dispose();
    }
}
