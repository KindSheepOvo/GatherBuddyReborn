using Dalamud.Game.ClientState.Conditions;
using System;
using System.Threading;

namespace GatherBuddy.Crafting;

internal static class VulcanRetainerPreview
{
    private sealed record Snapshot(ulong CharacterId, VulcanRetainerInventory Inventory);

    private static Snapshot? _snapshot;
    private static CancellationTokenSource? _lifetime;
    private static int _refreshPending;
    private static int _dirty = 1;
    private static int _revision;

    internal const string InventoryTooltip = "本次登录已核实的雇员库存。— 表示游戏尚未加载完整的库存。";
    internal static int Revision => Volatile.Read(ref _revision);

    internal static VulcanRetainerInventory? GetVerifiedInventory()
    {
        var characterId = Dalamud.PlayerState.ContentId;
        if (characterId == 0)
        {
            return null;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot?.CharacterId != characterId)
        {
            ScheduleRefresh();
            return null;
        }

        if (Volatile.Read(ref _dirty) != 0)
        {
            ScheduleRefresh();
        }

        return snapshot.Inventory.IsComplete ? snapshot.Inventory : null;
    }

    internal static void Initialize()
    {
        _lifetime = new CancellationTokenSource();
        Volatile.Write(ref _refreshPending, 0);
        Dalamud.ClientState.Logout += OnLogout;
        Dalamud.Conditions.ConditionChange += OnConditionChanged;
    }

    internal static void Dispose()
    {
        if (_lifetime is not { } lifetime)
        {
            return;
        }

        _lifetime = null;
        lifetime.Cancel();
        lifetime.Dispose();
        Dalamud.ClientState.Logout -= OnLogout;
        Dalamud.Conditions.ConditionChange -= OnConditionChanged;
        Clear();
    }

    internal static void Invalidate()
        => Volatile.Write(ref _dirty, 1);

    private static void Clear()
    {
        Volatile.Write(ref _snapshot, null);
        Interlocked.Increment(ref _revision);
    }

    private static void OnLogout(int type, int code)
        => Clear();

    private static void OnConditionChanged(ConditionFlag flag, bool enabled)
    {
        if (flag == ConditionFlag.OccupiedSummoningBell && !enabled)
        {
            Invalidate();
        }
    }

    private static void ScheduleRefresh()
    {
        if (_lifetime is not { } lifetime || Volatile.Read(ref _refreshPending) != 0
            || Interlocked.Exchange(ref _refreshPending, 1) != 0)
        {
            return;
        }

        _ = Dalamud.Framework.RunOnTick(() =>
        {
            try
            {
                Volatile.Write(ref _dirty, 0);
                var characterId = Dalamud.PlayerState.ContentId;
                if (characterId == 0 || !Dalamud.ClientState.IsLoggedIn)
                {
                    return;
                }

                var inventory = new VulcanRetainerInventory();
                try
                {
                    if (inventory.ReadRetainerList())
                    {
                        inventory.ReadCurrentGameSnapshots();
                    }
                }
                catch (Exception ex)
                {
                    inventory.Reset();
                    GatherBuddy.Log.Debug($"[Vulcan] 读取雇员预览库存失败: {ex.Message}");
                }

                // The published inventory is read-only to background preview builders.
                Volatile.Write(ref _snapshot, new Snapshot(characterId, inventory));
                Interlocked.Increment(ref _revision);
            }
            finally
            {
                Volatile.Write(ref _refreshPending, 0);
            }
        }, delay: TimeSpan.FromMilliseconds(200), cancellationToken: lifetime.Token);
    }
}
