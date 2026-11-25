using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.Crafting;

internal unsafe class RetainerTaskExecutor
{
    private enum Phase
    {
        InteractBell,
        WaitOccupied,
        WaitRetainerList,
        SelectRetainer,
        WaitRetainerMenu,
        SelectEntrustWithdraw,
        WaitInventory,
        WithdrawNextItem,
        FindItemSlot,
        DispatchRetrieveCommand,
        WaitNumericInput,
        InputNumericValue,
        WaitWithdrawComplete,
        CloseRetainerInventory,
        WaitInventoryClosed,
        SelectQuit,
        WaitQuit,
        CloseRetainerList,
        WaitListClosed,
        Complete,
        Aborted
    }

    private record struct WithdrawTarget(uint ItemId, int AmountHQ, int AmountNQ)
    {
        public int RemainingHQ = AmountHQ;
        public int RemainingNQ = AmountNQ;
        public int Total => RemainingHQ + RemainingNQ;
    }

    private Phase _phase = Phase.InteractBell;
    private DateTime _nextRetry = DateTime.MinValue;

    private readonly List<VulcanRetainerInventory.Retainer> _retainersToVisit = [];
    private int _retainerVisitIndex = 0;

    private List<WithdrawTarget> _currentRetainerItems = [];
    private int _currentItemIndex = 0;
    private bool _lookingForHQ = false;
    private int _foundSlotQty = 0;
    private InventoryType _foundSlotInventory = InventoryType.Invalid;
    private uint _foundSlotIndex = uint.MaxValue;
    private bool _foundSlotRequiresQuantityInput = false;
    private int _requestedQuantity;
    private bool _openedInventory;

    private int _addonRetryCount = 0;
    private const int MaxAddonRetries = 40;
    private readonly Dictionary<uint, int> _materials;
    private readonly Dictionary<uint, IngredientQualityDemand> _qualityTargets;
    private readonly HashSet<uint> _precraftItemIds;
    private readonly VulcanRetainerInventory _inventory;
    private readonly bool _verifyOnly;
    private RetainerBellNavigator? _verificationNavigator;
    private bool _closeRequested;

    public bool IsComplete => _phase == Phase.Complete && FailureReason == null;
    public bool IsAborted => _phase == Phase.Aborted || FailureReason != null;
    internal string? FailureReason { get; private set; }
    internal bool CloseRequested => _closeRequested;
    internal TimeSpan NextCheckDelay => TimeSpan.FromMilliseconds(Math.Max(100, (_nextRetry - DateTime.Now).TotalMilliseconds));

    public RetainerTaskExecutor(VulcanRetainerInventory inventory)
        : this(inventory, [], [])
    {
        _verifyOnly = true;
    }

    public RetainerTaskExecutor(
        VulcanRetainerInventory inventory,
        Dictionary<uint, int> materials,
        Dictionary<uint, IngredientQualityDemand> qualityTargets,
        HashSet<uint>? precraftItemIds = null)
    {
        _inventory = inventory;
        _materials = materials;
        _qualityTargets = qualityTargets;
        _precraftItemIds = precraftItemIds ?? [];
    }

    private bool BuildVisitPlan()
    {
        if (!_inventory.ReadRetainerList())
        {
            return false;
        }

        _inventory.ReadCurrentGameSnapshots();
        if (_verifyOnly)
        {
            _retainersToVisit.AddRange(_inventory.Retainers.Where(retainer => !_inventory.HasInventory(retainer.Id)));
            return true;
        }

        if (!_inventory.IsComplete)
        {
            FailureReason = "雇员库存需要重新核实。点击「继续」后重新计算缺口。";
            return true;
        }

        foreach (var (itemId, totalNeeded) in _materials)
        {
            var demand = _qualityTargets.TryGetValue(itemId, out var qualityDemand)
                ? qualityDemand
                : IngredientQualityDemand.FromPreferHQ(totalNeeded);

            var remainingDemand = demand;
            if (!_precraftItemIds.Contains(itemId))
            {
                var (inBagNQ, inBagHQ) = CraftingStockRun.GetInventoryCounts(itemId);
                remainingDemand = demand.ConsumeSplit(inBagNQ, inBagHQ, out _, out _);
            }

            foreach (var retainer in _inventory.Retainers)
            {
                if (remainingDemand.Total <= 0)
                {
                    break;
                }

                var (retainerNQ, retainerHQ) = _inventory.GetCounts(retainer.Id, itemId);
                remainingDemand = remainingDemand.ConsumeSplit(retainerNQ, retainerHQ, out var toTakeNQ, out var toTakeHQ);
                if (toTakeHQ <= 0 && toTakeNQ <= 0)
                {
                    continue;
                }

                if (!_perRetainerPlan.TryGetValue(retainer.Id, out var items))
                {
                    _perRetainerPlan[retainer.Id] = items = [];
                }

                items[itemId] = (toTakeHQ, toTakeNQ);
            }
        }

        _retainersToVisit.AddRange(_inventory.Retainers.Where(retainer => _perRetainerPlan.ContainsKey(retainer.Id)));
        return true;
    }

    private readonly Dictionary<ulong, Dictionary<uint, (int NeedHQ, int NeedNQ)>> _perRetainerPlan = [];

    internal void StopNavigation()
    {
        _verificationNavigator?.Stop();
        _verificationNavigator = null;
    }

    internal bool RequestClose()
    {
        if (_phase is Phase.InteractBell or Phase.Complete or Phase.Aborted)
        {
            return false;
        }

        _closeRequested = true;
        return true;
    }

    public CraftingTasks.TaskResult Tick()
    {
        if (DateTime.Now < _nextRetry)
        {
            return CraftingTasks.TaskResult.Retry;
        }

        if (_closeRequested)
        {
            _phase = _phase switch
            {
                Phase.WaitOccupied => _phase,
                _ when !Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell] => Phase.Complete,
                Phase.WaitRetainerList or Phase.SelectRetainer => Phase.CloseRetainerList,
                Phase.SelectEntrustWithdraw => Phase.SelectQuit,
                Phase.WithdrawNextItem or Phase.FindItemSlot or Phase.DispatchRetrieveCommand => Phase.CloseRetainerInventory,
                _ => _phase,
            };
        }

        return _phase switch
        {
            Phase.InteractBell => TickInteractBell(),
            Phase.WaitOccupied => TickWaitOccupied(),
            Phase.WaitRetainerList => TickWaitRetainerList(),
            Phase.SelectRetainer => TickSelectRetainer(),
            Phase.WaitRetainerMenu => TickWaitRetainerMenu(),
            Phase.SelectEntrustWithdraw => TickSelectEntrustWithdraw(),
            Phase.WaitInventory => TickWaitInventory(),
            Phase.WithdrawNextItem => TickWithdrawNextItem(),
            Phase.FindItemSlot => TickFindItemSlot(),
            Phase.DispatchRetrieveCommand => TickDispatchRetrieveCommand(),
            Phase.WaitNumericInput => TickWaitNumericInput(),
            Phase.InputNumericValue => TickInputNumericValue(),
            Phase.WaitWithdrawComplete => TickWaitWithdrawComplete(),
            Phase.CloseRetainerInventory => TickCloseRetainerInventory(),
            Phase.WaitInventoryClosed => TickWaitInventoryClosed(),
            Phase.SelectQuit => TickSelectQuit(),
            Phase.WaitQuit => TickWaitQuit(),
            Phase.CloseRetainerList => TickCloseRetainerList(),
            Phase.WaitListClosed => TickWaitListClosed(),
            Phase.Complete => CraftingTasks.TaskResult.Done,
            Phase.Aborted => CraftingTasks.TaskResult.Done,
            _ => CraftingTasks.TaskResult.Done
        };
    }

    private CraftingTasks.TaskResult TickInteractBell()
    {
        if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            StopNavigation();
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Already at bell, waiting for retainer list");
            _phase = Phase.WaitRetainerList;
            return CraftingTasks.TaskResult.Retry;
        }

        var navigator = _verificationNavigator;
        if (navigator != null)
        {
            navigator.Update();
            if (!navigator.IsComplete)
            {
                Delay(250);
                return CraftingTasks.TaskResult.Retry;
            }

            StopNavigation();
        }

        var bell = FindNearestBell();
        if (bell == null)
        {
            if (_verifyOnly && navigator == null && GatherBuddy.Config.VulcanRetainerBellConfig.AutoNavigateToRetainerBell
                && FindNearestBellForNavigation() is { } distantBell)
            {
                _verificationNavigator = new RetainerBellNavigator();
                if (_verificationNavigator.StartNavigation(distantBell))
                {
                    Delay(250);
                    return CraftingTasks.TaskResult.Retry;
                }

                StopNavigation();
            }

            return Abort("需要读取雇员库存。请靠近可用的传唤铃后点击「继续」。");
        }

        TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bell.Address);
        _phase = Phase.WaitOccupied;
        _addonRetryCount = 0;
        Delay(600);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitOccupied()
    {
        if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            _phase = Phase.WaitRetainerList;
            _addonRetryCount = 0;
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Talk dialog visible, dismissing");
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("未能打开传唤铃。请检查角色状态后点击「继续」。", 300);
        }

        return RetryAddon("未能打开传唤铃。请检查角色状态后点击「继续」。");
    }

    private CraftingTasks.TaskResult TickWaitRetainerList()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && addon->IsVisible)
        {
            if (!BuildVisitPlan())
            {
                return RetryAddon("游戏未能加载雇员名单。请结束当前雇员操作后点击「继续」。");
            }
            _retainerVisitIndex = 0;
            _addonRetryCount = 0;
            _phase = _retainersToVisit.Count == 0 ? Phase.CloseRetainerList : Phase.SelectRetainer;
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("游戏未能打开雇员名单。请结束当前雇员操作后点击「继续」。", 300);
        }

        return RetryAddon("游戏未能打开雇员名单。请结束当前雇员操作后点击「继续」。");
    }

    private CraftingTasks.TaskResult TickSelectRetainer()
    {
        if (_retainerVisitIndex >= _retainersToVisit.Count || FailureReason != null)
        {
            _phase = Phase.CloseRetainerList;
            return CraftingTasks.TaskResult.Retry;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
        {
            return RetryAddon("雇员名单未就绪。请结束当前雇员操作后点击「继续」。");
        }

        var entry = _retainersToVisit[_retainerVisitIndex];
        Callback.Fire(addon, true, 2, (uint)entry.SortedIndex);
        _addonRetryCount = 0;
        _phase = Phase.WaitRetainerMenu;
        Delay(300);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitRetainerMenu()
    {
        if (GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) &&
            menu->AtkUnitBase.IsVisible)
        {
            _phase = Phase.SelectEntrustWithdraw;
            _addonRetryCount = 0;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Dismissing Talk dialog before retainer menu");
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("未能打开雇员菜单。请结束当前雇员操作后点击「继续」。", 300);
        }

        return RetryAddon("未能打开雇员菜单。请结束当前雇员操作后点击「继续」。");
    }

    private CraftingTasks.TaskResult TickSelectEntrustWithdraw()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            return RetryAddon("未能打开雇员菜单。请结束当前雇员操作后点击「继续」。");
        }

        var entryCount = menu->PopupMenu.PopupMenu.EntryCount;
        if (entryCount == 0)
        {
            return RetryAddon("雇员菜单尚未加载。请结束当前雇员操作后点击「继续」。");
        }

        new AddonMaster.SelectString((nint)menu).Entries[0].Select();
        _addonRetryCount = 0;
        _phase = Phase.WaitInventory;
        Delay(200);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitInventory()
    {
        var entry = _retainersToVisit[_retainerVisitIndex];
        if (_verifyOnly ? _inventory.TryReadOpenedRetainer(entry.Id) : VulcanRetainerInventory.GetOpenedInventory(entry.Id, requireAllPages: true) != null)
        {
            _addonRetryCount = 0;
            if (_closeRequested || _verifyOnly)
            {
                _phase = Phase.CloseRetainerInventory;
                return CraftingTasks.TaskResult.Retry;
            }

            _openedInventory = true;
            _currentRetainerItems = BuildItemListForRetainer(entry.Id);
            _currentItemIndex = 0;
            _phase = Phase.WithdrawNextItem;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > MaxAddonRetries)
        {
            FailureReason = "游戏未能加载雇员物品栏。请点击「继续」重新核实库存。";
            _inventory.Invalidate(entry.Id);
            _phase = Phase.CloseRetainerInventory;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWithdrawNextItem()
    {
        ClearFoundSlot();
        while (_currentItemIndex < _currentRetainerItems.Count)
        {
            var target = _currentRetainerItems[_currentItemIndex];
            if (target.Total <= 0)
            {
                _currentItemIndex++;
                continue;
            }

            if (target.RemainingHQ > 0)
            {
                _lookingForHQ = true;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Withdrawing {target.RemainingHQ} HQ of item {target.ItemId}");
            }
            else
            {
                _lookingForHQ = false;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Withdrawing {target.RemainingNQ} NQ of item {target.ItemId}");
            }

            _phase = Phase.FindItemSlot;
            return CraftingTasks.TaskResult.Retry;
        }

        _phase = Phase.CloseRetainerInventory;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickFindItemSlot()
    {
        var target = _currentRetainerItems[_currentItemIndex];
        var itemId = target.ItemId;

        var inventoryManager = VulcanRetainerInventory.GetOpenedInventory(_retainersToVisit[_retainerVisitIndex].Id);
        if (inventoryManager == null)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] 搜索雇员物品栏时 InventoryManager 不可用");
            _phase = Phase.CloseRetainerInventory;
            return CraftingTasks.TaskResult.Retry;
        }

        foreach (var inv in VulcanRetainerInventory.Containers)
        {
            var container = inventoryManager->GetInventoryContainer(inv);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId || slot->Quantity <= 0 || (slot->Flags & InventoryItem.ItemFlags.Collectable) != 0)
                {
                    continue;
                }

                var slotIsHQ = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (slotIsHQ != _lookingForHQ)
                {
                    continue;
                }

                var wantQty = _lookingForHQ ? target.RemainingHQ : target.RemainingNQ;

                _foundSlotQty = (int)slot->Quantity;
                _foundSlotInventory = inv;
                _foundSlotIndex = (uint)i;
                _foundSlotRequiresQuantityInput = itemId <= 19 || wantQty < _foundSlotQty;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Found item {itemId} (HQ={slotIsHQ}) in {inv} slot {i}, qty={_foundSlotQty}, mode={(_foundSlotRequiresQuantityInput ? "quantity" : "all")}");

                _addonRetryCount = 0;
                _phase = Phase.DispatchRetrieveCommand;
                return CraftingTasks.TaskResult.Retry;
            }
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Item {itemId} (HQ={_lookingForHQ}) not found in retainer inventory, skipping quality pass");
        ClearFoundSlot();

        var t = _currentRetainerItems[_currentItemIndex];
        if (_lookingForHQ && t.RemainingNQ > 0)
        {
            _lookingForHQ = false;
            _phase = Phase.FindItemSlot;
        }
        else
        {
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
        }

        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickDispatchRetrieveCommand()
    {
        if (_foundSlotInventory == InventoryType.Invalid || _foundSlotIndex == uint.MaxValue)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] 分派取回命令前丢失了跟踪中的雇员栏位");
            _phase = Phase.FindItemSlot;
            return CraftingTasks.TaskResult.Retry;
        }

        var target = _currentRetainerItems[_currentItemIndex];
        var wantQty = _lookingForHQ ? target.RemainingHQ : target.RemainingNQ;
        if (ReadFoundSlotQuantity(target.ItemId) != _foundSlotQty)
        {
            _phase = Phase.FindItemSlot;
            return CraftingTasks.TaskResult.Retry;
        }

        if (_foundSlotRequiresQuantityInput)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] 正在从 {_foundSlotInventory} 的栏位 {_foundSlotIndex} 按数量取回物品 {target.ItemId} (需要={wantQty}, 栏位数量={_foundSlotQty})");
            if (!RetainerItemCommandDispatcher.TryRetrieveQuantity(_foundSlotInventory, _foundSlotIndex))
            {
                return HandleRetrieveDispatchFailure("按数量取回");
            }

            _addonRetryCount = 0;
            _phase = Phase.WaitNumericInput;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] 正在从 {_foundSlotInventory} 的栏位 {_foundSlotIndex} 全部取回物品 {target.ItemId} (需要={wantQty}, 栏位数量={_foundSlotQty})");
        if (!RetainerItemCommandDispatcher.TryRetrieve(_foundSlotInventory, _foundSlotIndex))
        {
            return HandleRetrieveDispatchFailure("全部取回");
        }

        _requestedQuantity = _foundSlotQty;
        _addonRetryCount = 0;
        _phase = Phase.WaitWithdrawComplete;
        Delay(300);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitNumericInput()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) && input->IsVisible)
        {
            _phase = Phase.InputNumericValue;
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            var target = _currentRetainerItems[_currentItemIndex];
            GatherBuddy.Log.Warning($"[RetainerTaskExecutor] 从 {_foundSlotInventory} 的栏位 {_foundSlotIndex} 按数量取回物品 {target.ItemId} 后, InputNumeric 未出现");
            return FailAndCloseInventory("游戏未打开取回数量窗口。请点击「继续」重新计算缺口。");
        }

        Delay(100);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickInputNumericValue()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) || !input->IsVisible)
        {
            var failedTarget = _currentRetainerItems[_currentItemIndex];
            GatherBuddy.Log.Warning($"[RetainerTaskExecutor] 输入物品 {failedTarget.ItemId} 的取回数量前 InputNumeric 已关闭");
            return FailAndCloseInventory("取回数量窗口已关闭。请点击「继续」重新计算缺口。");
        }

        if (_closeRequested)
        {
            input->Close(true);
            _phase = Phase.WaitWithdrawComplete;
            Delay(150);
            return CraftingTasks.TaskResult.Retry;
        }

        var target = _currentRetainerItems[_currentItemIndex];
        var wantQty = _lookingForHQ ? target.RemainingHQ : target.RemainingNQ;
        var value = Math.Min(wantQty, _foundSlotQty);
        if (ReadFoundSlotQuantity(target.ItemId) != _foundSlotQty)
        {
            input->Close(true);
            return FailAndCloseInventory("雇员栏位已变化。请点击「继续」重新计算缺口。");
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Inputting quantity {value} (want={wantQty}, slot={_foundSlotQty})");
        Callback.Fire(input, true, value);

        _requestedQuantity = value;
        _addonRetryCount = 0;
        _phase = Phase.WaitWithdrawComplete;
        Delay(400);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitWithdrawComplete()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) && input->IsVisible)
        {
            if (++_addonRetryCount > 20)
            {
                input->Close(true);
                return FailAndCloseInventory("取回数量未确认。请检查背包空间后点击「继续」。");
            }

            Delay(100);
            return CraftingTasks.TaskResult.Retry;
        }

        if (_requestedQuantity == 0 && _closeRequested)
        {
            _phase = Phase.CloseRetainerInventory;
            return CraftingTasks.TaskResult.Retry;
        }

        var target = _currentRetainerItems[_currentItemIndex];
        var remainingInSlot = ReadFoundSlotQuantity(target.ItemId);
        if (remainingInSlot < 0 || _foundSlotQty - remainingInSlot < _requestedQuantity)
        {
            if (++_addonRetryCount > 20)
            {
                return FailAndCloseInventory("游戏未确认取回材料。请检查背包空间后点击「继续」。");
            }

            Delay(150);
            return CraftingTasks.TaskResult.Retry;
        }

        if (_lookingForHQ)
        {
            target.RemainingHQ -= _requestedQuantity;
        }
        else
        {
            target.RemainingNQ -= _requestedQuantity;
        }

        _requestedQuantity = 0;
        _currentRetainerItems[_currentItemIndex] = target;
        if (_closeRequested)
        {
            _phase = Phase.CloseRetainerInventory;
            return CraftingTasks.TaskResult.Retry;
        }

        if (_lookingForHQ && target.RemainingHQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] More HQ needed ({target.RemainingHQ}), re-entering FindItemSlot");
            ClearFoundSlot();
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        if (!_lookingForHQ && target.RemainingNQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] More NQ needed ({target.RemainingNQ}), re-entering FindItemSlot");
            ClearFoundSlot();
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        if (_lookingForHQ && target.RemainingHQ <= 0 && target.RemainingNQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] HQ pass done, switching to NQ pass for item {target.ItemId}");
            _lookingForHQ = false;
            ClearFoundSlot();
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }
        ClearFoundSlot();

        _currentItemIndex++;
        _phase = Phase.WithdrawNextItem;
        Delay(200);
        return CraftingTasks.TaskResult.Retry;
    }

    private int ReadFoundSlotQuantity(uint itemId)
    {
        var inventory = VulcanRetainerInventory.GetOpenedInventory(_retainersToVisit[_retainerVisitIndex].Id);
        var container = inventory == null ? null : inventory->GetInventoryContainer(_foundSlotInventory);
        if (container == null || !container->IsLoaded || _foundSlotIndex >= (uint)container->Size)
        {
            return -1;
        }

        var slot = container->GetInventorySlot((int)_foundSlotIndex);
        return slot != null && slot->ItemId == itemId && (slot->Flags & InventoryItem.ItemFlags.Collectable) == 0
            && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == _lookingForHQ
            ? (int)slot->Quantity : 0;
    }

    private CraftingTasks.TaskResult HandleRetrieveDispatchFailure(string commandName)
    {
        var target = _currentItemIndex < _currentRetainerItems.Count
            ? _currentRetainerItems[_currentItemIndex]
            : default;
        GatherBuddy.Log.Warning($"[RetainerTaskExecutor] 从 {_foundSlotInventory} 的栏位 {_foundSlotIndex} 为物品 {target.ItemId} 分派{commandName}命令失败, 正在关闭当前雇员物品栏");
        ClearFoundSlot();
        return FailAndCloseInventory("未能取回雇员材料。请点击「继续」重新计算缺口。");
    }

    private CraftingTasks.TaskResult FailAndCloseInventory(string reason)
    {
        FailureReason = reason;
        _phase = Phase.CloseRetainerInventory;
        return CraftingTasks.TaskResult.Retry;
    }

    private void ClearFoundSlot()
    {
        _foundSlotInventory = InventoryType.Invalid;
        _foundSlotIndex = uint.MaxValue;
        _foundSlotQty = 0;
        _foundSlotRequiresQuantityInput = false;
    }

    private CraftingTasks.TaskResult TickCloseRetainerInventory()
    {
        if (_openedInventory)
        {
            _openedInventory = false;
            var retainerId = _retainersToVisit[_retainerVisitIndex].Id;
            if (!_inventory.TryReadOpenedRetainer(retainerId))
            {
                _inventory.Invalidate(retainerId);
                FailureReason ??= "无法确认取回后的雇员库存。请点击「继续」重新核实。";
            }
        }

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
        { _phase = Phase.SelectQuit; return CraftingTasks.TaskResult.Retry; }

        var retainerAgent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent != null && retainerAgent->IsAgentActive())
        {
            retainerAgent->Hide();
            _addonRetryCount = 0;
            _phase = Phase.WaitInventoryClosed;
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        _phase = Phase.SelectQuit;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitInventoryClosed()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
        { _phase = Phase.SelectQuit; return CraftingTasks.TaskResult.Retry; }
        var retainerAgent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
        {
            _phase = Phase.SelectQuit;
            _addonRetryCount = 0;
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Retainer agent did not close, continuing anyway");
            _phase = Phase.SelectQuit;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickSelectQuit()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            return RetryAddon("无法关闭雇员菜单。请退出传唤铃后点击「继续」。");
        }

        var quitText = GetAddonText(2383);
        var entryCount = menu->PopupMenu.PopupMenu.EntryCount;

        for (var i = 0; quitText.Length > 0 && i < entryCount; i++)
        {
            var label = MemoryHelper.ReadSeStringNullTerminated((nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value).TextValue;
            if (!label.Contains(quitText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            new AddonMaster.SelectString((nint)menu).Entries[i].Select();
            _retainerVisitIndex++;
            _addonRetryCount = 0;
            _phase = Phase.WaitQuit;
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        return RetryAddon("找不到雇员菜单的退出选项。请退出传唤铃后点击「继续」。");
    }

    private CraftingTasks.TaskResult TickWaitQuit()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Dismissing Talk dialog after Quit");
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("无法结束雇员对话。请退出传唤铃后点击「继续」。", 300);
        }

        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            _phase = Phase.SelectRetainer;
            _addonRetryCount = 0;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        return RetryAddon("无法结束雇员对话。请退出传唤铃后点击「继续」。");
    }

    private CraftingTasks.TaskResult TickCloseRetainerList()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Dismissing Talk dialog before closing RetainerList");
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("无法退出传唤铃。请结束当前雇员操作后点击「继续」。", 300);
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
        {
            if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
            {
                return RetryAddon("无法退出传唤铃。请结束当前雇员操作后点击「继续」。");
            }

            _phase = Phase.Complete;
            return CraftingTasks.TaskResult.Done;
        }

        Callback.Fire(addon, true, -1);
        _addonRetryCount = 0;
        _phase = Phase.WaitListClosed;
        Delay(300);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitListClosed()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            new AddonMaster.Talk((nint)talk).Click();
            return RetryAddon("无法退出传唤铃。请结束当前雇员操作后点击「继续」。", 300);
        }

        if (!Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            _phase = Phase.Complete;
            return CraftingTasks.TaskResult.Done;
        }

        return RetryAddon("无法退出传唤铃。请结束当前雇员操作后点击「继续」。");
    }

    private List<WithdrawTarget> BuildItemListForRetainer(ulong retainerId)
    {
        var list = new List<WithdrawTarget>();
        if (!_perRetainerPlan.TryGetValue(retainerId, out var items))
        {
            return list;
        }

        foreach (var (itemId, amounts) in items.OrderByDescending(kv => kv.Value.NeedHQ > 0 ? 1 : 0))
        {
            if (amounts.NeedHQ > 0 || amounts.NeedNQ > 0)
            {
                list.Add(new WithdrawTarget(itemId, amounts.NeedHQ, amounts.NeedNQ));
            }
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Built item list for retainer {retainerId}: {list.Count} item(s)");
        return list;
    }

    public static IGameObject? FindNearestBellForNavigation()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        var bellName = GetBellName();
        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.HousingEventObject && obj.ObjectKind != ObjectKind.EventObj)
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (!name.Equals(bellName, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("リテイナーベル", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!obj.IsTargetable)
            {
                continue;
            }

            var distance = Vector3.Distance(obj.Position, player.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    private static IGameObject? FindNearestBell()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        var bellName = GetBellName();

        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.HousingEventObject && obj.ObjectKind != ObjectKind.EventObj)
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (!name.Equals(bellName, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("リテイナーベル", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var maxDist = obj.ObjectKind == ObjectKind.HousingEventObject ? 6.5f : 4.75f;
            if (Vector3.Distance(obj.Position, player.Position) > maxDist)
            {
                continue;
            }

            if (!obj.IsTargetable)
            {
                continue;
            }

            return obj;
        }

        return null;
    }

    private static string GetBellName()
    {
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<EObjName>();
            if (sheet != null && sheet.TryGetRow(2000401, out var row))
            {
                return row.Singular.ExtractText();
            }
        }
        catch { }
        return "Summoning Bell";
    }

    private static string GetAddonText(uint rowId)
    {
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            if (sheet != null && sheet.TryGetRow(rowId, out var row))
            {
                return row.Text.ExtractText();
            }
        }
        catch { }
        return string.Empty;
    }

    private void Delay(int ms)
    {
        _nextRetry = DateTime.Now.AddMilliseconds(ms);
    }

    private CraftingTasks.TaskResult RetryAddon(string reason, int delay = 150)
    {
        if (++_addonRetryCount > MaxAddonRetries)
        {
            return Abort(reason);
        }

        Delay(delay);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult Abort(string reason)
    {
        FailureReason ??= reason;
        _phase = Phase.Aborted;
        return CraftingTasks.TaskResult.Done;
    }

    internal static Dictionary<uint, IngredientQualityDemand> ComputeQualityTargets(
        Dictionary<uint, int> materials,
        List<CraftingListItem> expandedQueue)
    {
        var targets = materials.Keys.ToDictionary(itemId => itemId, _ => default(IngredientQualityDemand));

        foreach (var queueItem in expandedQueue)
        {
            var recipe = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipe == null)
            {
                continue;
            }

            var qualityPolicy = queueItem.QualityPolicy ?? CraftingQualityPolicyResolver.Resolve(recipe.Value, queueItem.CraftSettings);
            foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
            {
                if (!targets.ContainsKey(itemId))
                {
                    continue;
                }

                targets[itemId] = targets[itemId].Add(qualityPolicy.GetDemand(itemId).Scale(Math.Max(1, queueItem.Quantity)));
            }
        }

        foreach (var (itemId, totalNeeded) in materials)
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
}
