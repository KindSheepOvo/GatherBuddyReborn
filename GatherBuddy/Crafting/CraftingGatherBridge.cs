using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Automation;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public static partial class CraftingGatherBridge
{
    private static AutoGatherList? _gatherList;
    private static global::GatherBuddy.GatherBuddy? _plugin;
    private static uint _recipeIdToCraft = 0;
    private static bool _waitingForGatherComplete = false;
    private static DateTime _jobSwitchTime = DateTime.MinValue;
    private static bool _waitingForJobSwitch = false;
    private static CraftingQueueProcessor? _queueProcessor = null;
    private static CraftingExecutionPlan? _activeExecutionPlan = null;
    private static bool _isQueueMode = false;
    private static int? _ephemeralListId = null;
    private static bool _waitingForCollectables = false;
    private static bool _collectablesStartPending = false;
    private static bool _waitingForCollectablesHomeReturn;
    private static bool _collectablesHomeReturnStarted;
    private static DateTime _nextCollectablesRetry = DateTime.MinValue;
    private static DateTime _lastCollectablesWaitLog = DateTime.MinValue;
    private static DateTime _lastCollectablesExitAttempt = DateTime.MinValue;

    public static bool PreserveListOnDisable { get; set; } = false;

    public static void Initialize(global::GatherBuddy.GatherBuddy plugin)
    {
        _plugin = plugin;
        VulcanRetainerPreview.Initialize();
    }

    private static int RoundUpToBatchSize(int quantity, int batchSize)
        => batchSize <= 1
            ? quantity
            : (int)Math.Ceiling((double)quantity / batchSize) * batchSize;

    public static void BindCollectableManager(CollectableManager manager)
    {
        manager.OnFinishCollecting -= OnCollectablesFinished;
        manager.OnError -= OnCollectablesError;
        manager.OnFinishCollecting += OnCollectablesFinished;
        manager.OnError += OnCollectablesError;
    }

    public static uint RecipeToCraft => _recipeIdToCraft;
    public static bool WaitingForGatherComplete => _stockRun == null && _waitingForGatherComplete;

    public static AutoGatherList? GetTemporaryGatherList() => _gatherList;
    public static CraftingExecutionPlan? GetActiveExecutionPlan()
        => _activeExecutionPlan;

    public static CraftingExecutionPlan? GetActiveExecutionPlan(int listId)
        => _activeExecutionPlan != null && _activeExecutionPlan.MatchesList(listId)
            ? _activeExecutionPlan
            : null;

    public static void DeleteTemporaryGatherList()
    {
        if (_gatherList != null && _plugin != null)
        {
            try
            {
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Deleted temporary gather list: {_gatherList.Name}");
                _gatherList = null;
                _plugin.AutoGatherListsManager.SetCraftingGatherList(null);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to delete temporary gather list: {ex.Message}");
            }
        }
    }

    public static void CreatePersistentGatherList(string listName, Dictionary<uint, int> materials)
    {
        if (_plugin == null)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot create gather list: plugin not initialized");
            return;
        }

        try
        {
            var gatherList = new AutoGatherList()
            {
                Name = listName,
                Enabled = false
            };

            foreach (var (itemId, quantity) in materials)
            {
                var gatherQuantity = GetCraftingGatherTargetQuantity(itemId, quantity, out var gatherItemId);
                if (gatherQuantity <= 0)
                {
                    continue;
                }

                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                {
                    gatherList.Add(gatherable, (uint)gatherQuantity);
                }
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                {
                    gatherList.Add(fish, (uint)gatherQuantity);
                }
                else
                {
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
                }
            }

            if (gatherList.Items.Count > 0)
            {
                _plugin.AutoGatherListsManager.AddList(gatherList);
                _plugin.AutoGatherListsManager.SetActiveItems();
                GatherBuddy.Log.Information($"[CraftingGatherBridge] Created gather list '{listName}' with {gatherList.Items.Count} items.");
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] No gatherable items found for list '{listName}'.");
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingGatherBridge] Failed to create gather list '{listName}': {ex.Message}");
        }
    }

    private static int GetCraftingGatherTargetQuantity(uint itemId, int quantity, out uint gatherItemId)
    {
        gatherItemId = itemId;
        if (quantity <= 0)
        {
            return 0;
        }

        if (!AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
        {
            return quantity;
        }

        var approvedDeficit = Math.Max(0, quantity - GetInventoryCount(itemId));
        if (approvedDeficit <= 0)
        {
            return 0;
        }

        gatherItemId = rawItemId;
        var batchSize = AutoGather.Helpers.Diadem.ApprovedInspectionBatchSizes.TryGetValue(itemId, out var configuredBatchSize) && configuredBatchSize > 0
            ? (int)configuredBatchSize
            : 1;
        return RoundUpToBatchSize(approvedDeficit, batchSize);
    }

    public static void Update()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            if (_queueProcessor.NeedsUpdate)
            {
                _queueProcessor.Update();
            }

            if (_stockRun == null && _queueProcessor.CurrentState == CraftingQueueProcessor.QueueState.Complete
             && !_queueProcessor.HasPendingTasks())
            {
                GatherBuddy.Log.Information("[CraftingGatherBridge] All completion tasks done, cleaning up");
                GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
                _queueProcessor.Reset();
                _queueProcessor = null;
                _activeExecutionPlan = null;
                _isQueueMode = false;

                if (_ephemeralListId.HasValue)
                {
                    GatherBuddy.Log.Information($"[CraftingGatherBridge] Deleting ephemeral crafting list {_ephemeralListId.Value}");
                    GatherBuddy.CraftingListManager.DeleteList(_ephemeralListId.Value);
                    _ephemeralListId = null;
                }
            }
        }

        if (!_waitingForJobSwitch)
        {
            return;
        }

        var timeSinceSwitch = (DateTime.Now - _jobSwitchTime).TotalSeconds;
        if (timeSinceSwitch >= 2)
        {
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Job switch wait complete, retrying gather-to-craft");
            _waitingForJobSwitch = false;
            _jobSwitchTime = DateTime.MinValue;
            OnGatherComplete();
        }
    }

    public static void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            var wasCrafting = _queueProcessor.CurrentState == CraftingQueueProcessor.QueueState.Crafting;
            _queueProcessor.OnCraftFinished(recipe, cancelled);
            if (wasCrafting)
            {
                MarkStockPlanDirty();
            }
        }
    }

    public static void StartGatherAndCraft(uint recipeId, Dictionary<uint, int> missing)
    {
        _isQueueMode = false;
        _recipeIdToCraft = recipeId;
        _waitingForGatherComplete = true;
        CreateGatherListForMissingIngredients(missing);
    }

    private static void StartQueue(CraftingExecutionPlan executionPlan, CraftingListConsumableSettings? listConsumables = null, int? ephemeralListId = null)
    {
        StopTemporaryGathering();
        _queueProcessor?.Reset();
        _isQueueMode = true;
        _ephemeralListId = ephemeralListId;
        _activeExecutionPlan = executionPlan;
        ResetCollectablesInterruptionState();
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.QueueCompleted += OnQueueCompleted;
        if (_stockRun != null)
        {
            _queueProcessor.StateChanged += OnStockQueueStateChanged;
            _queueProcessor.StockCheckpointAvailable += OnStockCheckpoint;
        }
        _waitingForGatherComplete = true;
        GatherBuddy.Log.Information($"[CraftingGatherBridge] Starting queue automation with {executionPlan.QueueView.Count} recipes, retainerRestock={executionPlan.RetainerRestock}");
        _queueProcessor.StartQueue(executionPlan, listConsumables, GatherBuddy.RaphaelSolveCoordinator);
        var hasRetainerWork = _queueProcessor.CurrentState != CraftingQueueProcessor.QueueState.WaitingForGather;
        if (!hasRetainerWork)
        {
            CreateGatherListForMissingIngredients(executionPlan.Materials);
        }

        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor, restoreWindow: _stockRun == null);
    }

    public static void CreateGatherListForMissingIngredients(Dictionary<uint, int> missing)
    {
        try
        {
            _gatherList = new AutoGatherList()
            {
                Name = "制作材料 (自动生成)",
                Enabled = true,
                UsesRetainerInventory = false
            };

            foreach (var (itemId, quantity) in missing)
            {
                var gatherQuantity = GetCraftingGatherTargetQuantity(itemId, quantity, out var gatherItemId);
                if (gatherQuantity <= 0)
                {
                    continue;
                }

                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                {
                    _gatherList.Add(gatherable, (uint)gatherQuantity);
                }
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                {
                    _gatherList.Add(fish, (uint)gatherQuantity);
                }
                else
                {
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
                }
            }

            if (_gatherList.Items.Count > 0 && _plugin != null)
            {
                _plugin.AutoGatherListsManager.SetCraftingGatherList(_gatherList);

                if (IsGatheringComplete())
                {
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather list created but all items already in inventory, proceeding directly to crafting");
                    OnGatherComplete();
                }
                else
                {
                    _waitingForGatherComplete = true;
                    GatherBuddy.AutoGather.Enabled = true;
                    if (!GatherBuddy.AutoGather.Enabled)
                    {
                        OnGatherEnabledChanged(enabled: false);
                        return;
                    }

                    GatherBuddy.Log.Information($"已创建包含 {_gatherList.Items.Count} 个物品的制作采集清单，开始自动采集。");
                }
            }
            else
            {
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] No gatherable items needed, proceeding directly to crafting");
                OnGatherComplete();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"创建采集清单失败: {ex.Message}");
        }
    }

    public static void OnGatherComplete()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            _waitingForGatherComplete = false;
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather complete for queue mode");
            _queueProcessor.OnGatherComplete();
            return;
        }

        if (_recipeIdToCraft == 0)
        {
            return;
        }

        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null || !recipeSheet.TryGetRow(_recipeIdToCraft, out var recipe))
        {
            GatherBuddy.Log.Error($"找不到配方 {_recipeIdToCraft}");
            _recipeIdToCraft = 0;
            _waitingForGatherComplete = false;
            return;
        }

        var requiredCraftJob = (uint)(recipe.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

        if (currentJob != requiredCraftJob)
        {
            if (!_waitingForJobSwitch)
            {
                GatherBuddy.Log.Information($"为制作从职业 {currentJob} 切换到职业 {requiredCraftJob}");
                SwitchJob(requiredCraftJob);
                _jobSwitchTime = DateTime.Now;
                _waitingForJobSwitch = true;
            }
            return;
        }

        _waitingForGatherComplete = false;
        _waitingForJobSwitch = false;
        GatherBuddy.Log.Information($"采集完成，开始为配方 {_recipeIdToCraft} 制作");

        DeleteTemporaryGatherList();

        CraftingGameInterop.StartCraft(recipe, 1);
        _recipeIdToCraft = 0;
    }

    private static unsafe void SwitchJob(uint jobId)
    {
        try
        {
            var gearsetModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Error("无法获取套装模块");
                return;
            }

            if (GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, jobId, out var gearsetIndex))
            {
                gearsetModule->EquipGearset(gearsetIndex);
                GatherBuddy.Log.Information($"已装备职业 {jobId} 的装备套装 {gearsetIndex}");
                return;
            }

            GatherBuddy.Log.Warning($"未找到职业 {jobId} 的套装");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"切换职业失败: {ex.Message}");
        }
    }

    public static bool IsGatheringComplete()
    {
        if (_gatherList == null)
        {
            return _waitingForGatherComplete;
        }

        var allComplete = true;
        foreach (var item in _gatherList.Items)
        {
            var have = GetInventoryCount(item.ItemId);
            var needed = _gatherList.Quantities.TryGetValue(item, out var qty) ? qty : 0;
            if (have < needed)
            {
                allComplete = false;
                break;
            }
        }

        return allComplete;
    }

    private static unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                return 0;
            }

            return inventory->GetInventoryItemCount(itemId, false, false, false);
        }
        catch
        {
            return 0;
        }
    }

    public static void TestRepairSystem()
    {
        if (_queueProcessor != null && _isQueueMode)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot test repair - queue is already running");
            return;
        }

        GatherBuddy.Log.Information("[CraftingGatherBridge] Starting repair system test");
        _isQueueMode = true;
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.TestRepair();

        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor);
    }

    private static void OnQueueCompleted()
    {
        GatherBuddy.Log.Information("[CraftingGatherBridge] Queue completed, will clean up after tasks finish");
    }

    private static bool TryBeginStockTurnIn(CraftingStockRun run)
    {
        if (!_collectablesCheckPending)
        {
            return false;
        }

        _collectablesCheckPending = false;
        var config = GatherBuddy.Config.CollectableConfig;
        if (!run.AllowCollectableTurnIn || !config.AutoTurnInCollectables || DateTime.UtcNow < _nextCollectablesRetry
            || GatherBuddy.CollectableManager is not { IsRunning: false } || !CollectableTurnInRequirements.IsAvailable
            || !CollectableInventoryHelper.GetThresholdState(config).ThresholdReached)
        {
            return false;
        }

        StopTemporaryGathering();
        _queueProcessor?.Pause();
        _collectablesStartPending = true;
        SetStockStatus("准备交纳背包中的收藏品");
        ContinueStockTurnIn(run);
        return true;
    }

    private static void ContinueStockTurnIn(CraftingStockRun run)
    {
        if (!GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables || !CollectableTurnInRequirements.IsAvailable)
        {
            ResumeStockAfterCollectables(run);
            return;
        }

        if (GatherBuddy.CollectableManager is not { IsRunning: false } manager)
        {
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        TryExitCraftingUiForCollectables();
        if (!IsReadyToStartCollectables(out var waitReason) || IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        if (manager.Start(CollectableRunSource.VulcanQueue, returnHomeAfterCompletion: true))
        {
            _collectablesStartPending = false;
            _waitingForCollectables = true;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
            SetStockStatus("正在交纳收藏品");
        }
        else if (IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            ScheduleStockWork(ReadinessRetryDelay);
        }
        else
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to start collectables interruption: {manager.StatusText}");
            _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
            ResumeStockAfterCollectables(run);
        }
    }

    private static void TryExitCraftingUiForCollectables()
    {
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleBetween
         && (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal || !IsCraftingAddonVisible("RecipeNote")))
        {
            return;
        }

        if (_lastCollectablesExitAttempt != DateTime.MinValue
         && DateTime.UtcNow - _lastCollectablesExitAttempt < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        _lastCollectablesExitAttempt = DateTime.UtcNow;
        CraftingTasks.TaskExitCraft();
    }

    private static bool IsReadyToStartCollectables(out string waitReason)
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
        {
            waitReason = "区域切换仍在进行中";
            return false;
        }

        if (Lifestream.Enabled && Lifestream.IsBusy())
        {
            waitReason = "Lifestream 仍在忙碌";
            return false;
        }

        if (!GenericHelpers.IsScreenReady())
        {
            waitReason = "画面尚未就绪";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            waitReason = "制作动作仍在执行中";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
        {
            waitReason = "制作准备仍在进行中";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.Crafting])
        {
            waitReason = $"制作状态仍为 {CraftingGameInterop.CurrentState}";
            return false;
        }

        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal)
        {
            waitReason = $"制作尚未回到 IdleNormal ({CraftingGameInterop.CurrentState})";
            return false;
        }

        if (IsCraftingAddonVisible("RecipeNote") || IsCraftingAddonVisible("Synthesis") || IsCraftingAddonVisible("SynthesisSimple") || IsCraftingAddonVisible("WKSRecipeNotebook"))
        {
            waitReason = "制作窗口仍然可见";
            return false;
        }

        waitReason = string.Empty;
        return true;
    }

    private static unsafe bool IsCraftingAddonVisible(string addonName)
    {
        var addon = (AtkUnitBase*)(nint)Dalamud.GameGui.GetAddonByName(addonName);
        return addon != null && addon->IsVisible;
    }

    private static bool IsWaitingForCollectablesRouteData(out string waitReason)
    {
        if (!CollectableTurnInRouteResolver.HasLookupData)
        {
            waitReason = string.Empty;
            return false;
        }

        var collectableNpcIds = CollectableTurnInRouteResolver.GetCollectableNpcIds();
        if (collectableNpcIds.Count == 0)
        {
            waitReason = string.Empty;
            return false;
        }

        VendorNpcLocationCache.InitializeAsync(collectableNpcIds);
        if (VendorNpcLocationCache.IsInitialized)
        {
            waitReason = string.Empty;
            return false;
        }

        waitReason = VendorNpcLocationCache.IsInitializing
            ? $"收藏品路线位置仍在加载 ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount} 个 NPC 已解析)"
            : "收藏品路线位置仍在加载";
        return true;
    }

    private static void LogCollectablesWaitReason(string waitReason)
    {
        if (_lastCollectablesWaitLog != DateTime.MinValue && DateTime.UtcNow - _lastCollectablesWaitLog < TimeSpan.FromSeconds(10))
        {
            return;
        }

        GatherBuddy.Log.Debug($"[CraftingGatherBridge] Waiting to start collectables interruption: {waitReason}");
        _lastCollectablesWaitLog = DateTime.UtcNow;
    }

    private static void OnCollectablesFinished()
    {
        if (_waitingForCollectables && _stockRun is { } run)
        {
            ResumeStockAfterCollectables(run);
        }
    }

    private static void ResumeStockAfterCollectables(CraftingStockRun run)
    {
        ResetCollectablesInterruptionState();
        _collectablesCheckPending = false;
        DiscardStockQueue();
        SetStockStatus(run.Paused ? "制作清单已暂停" : "正在按当前库存计算剩余制作");
        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null, restoreWindow: false);
        GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
        MarkStockPlanDirty();
    }

    private static void OnCollectablesError(string error)
    {
        if (!_waitingForCollectables || _stockRun is not { } run)
        {
            return;
        }

        GatherBuddy.Log.Error($"[CraftingGatherBridge] Collectables interruption failed: {error}");
        var config = GatherBuddy.Config.CollectableConfig;
        if (!config.AutoTurnInCollectables && !string.IsNullOrWhiteSpace(config.AutoTurnInHardFailReason))
        {
            _waitingForCollectables = false;
            _waitingForCollectablesHomeReturn = true;
            SetStockStatus("已停止交纳收藏品，正在返回家园");
            ScheduleStockWork(InventorySettleDelay);
            return;
        }

        _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
        ResumeStockAfterCollectables(run);
    }

    private static void ContinueCollectablesHomeReturn(CraftingStockRun run)
    {
        if (!_collectablesHomeReturnStarted)
        {
            if (HomeNavigationHelper.TryStartReturnHome(out var error))
            {
                _collectablesHomeReturnStarted = true;
                ScheduleStockWork(ReadinessRetryDelay);
                return;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                ScheduleStockWork(ReadinessRetryDelay);
                return;
            }

            GatherBuddy.Log.Warning($"[CraftingGatherBridge] {error}");
        }
        else if (!HomeNavigationHelper.IsReturnComplete())
        {
            ScheduleStockWork(ReadinessRetryDelay);
            return;
        }

        ResumeStockAfterCollectables(run);
    }

    private static void OnStockTurnInStopped()
    {
        if (_waitingForCollectables && _stockRun != null)
        {
            ResetCollectablesInterruptionState();
            PauseStockRun("收藏品交纳已停止。点击「继续」按当前库存重新计算。");
            GatherBuddy.CraftingStatusWindow?.OpenOrRestore();
        }
    }

    public static void StopQueue()
    {
        if (_stockRun != null)
        {
            FinishStockRun(completed: false);
            return;
        }

        if (_queueProcessor != null)
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] Stopping queue processor");
            ResetCollectablesInterruptionState();
            _ephemeralListId = null;
            GatherBuddy.AutoGather.Enabled = false;
            CraftingGameInterop.CancelCurrentCraft();
            DeleteTemporaryGatherList();
            _queueProcessor.Reset();
            _queueProcessor = null;
            _activeExecutionPlan = null;
            _isQueueMode = false;
            GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
        }
        else
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] No queue processor running");
        }
    }

    public static void PauseQueue(string reason)
    {
        if (_isQueueMode && _queueProcessor is { Paused: false })
        {
            _queueProcessor.Pause(reason);
        }
    }

    private static void ResetCollectablesInterruptionState()
    {
        _collectablesStartPending = false;
        _waitingForCollectables = false;
        _waitingForCollectablesHomeReturn = false;
        _collectablesHomeReturnStarted = false;
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
    }
}
