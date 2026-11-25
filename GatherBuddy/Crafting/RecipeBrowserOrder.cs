using GatherBuddy.Plugin;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace GatherBuddy.Crafting;

internal sealed record RecipeBrowserOrder(
    Guid Id, ulong CharacterId, uint RecipeId, uint ItemId, string Name, int Quantity, int TargetCount,
    bool QuickSynth, bool RetainerRestock, RecipeCraftSettings? Settings)
{
    public CraftingListDefinition CreateList()
        => CreateList(RecipeId, Name, Quantity, QuickSynth, RetainerRestock, Settings);

    public static CraftingListDefinition CreateList(uint recipeId, string name, int quantity, bool quickSynth,
        bool retainerRestock, RecipeCraftSettings? settings)
        => new()
        {
            ID = -1,
            Name = name,
            SkipIfEnough = true,
            SkipFinalIfEnough = true,
            RetainerRestock = retainerRestock,
            Recipes =
            [
                new CraftingListItem(recipeId, quantity)
                {
                    IsOriginalRecipe = true,
                    CraftSettings = settings?.Clone(),
                    Options = new ListItemOptions { NQOnly = quickSynth },
                },
            ],
        };
}

internal readonly record struct RecipeBrowserOrderState(ulong CharacterId, RecipeBrowserOrder? Order, bool ReadFailed = false);

internal static class RecipeBrowserOrderStore
{
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<ulong, RecipeBrowserOrderState> Cache = [];

    public static RecipeBrowserOrderState Get(ulong characterId, bool refresh = false)
    {
        lock (CacheLock)
        {
            if (!refresh && Cache.TryGetValue(characterId, out var state))
            {
                return state;
            }

            try
            {
                state = Read(characterId, GetPath(characterId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                GatherBuddy.Log.Warning($"[Vulcan] 无法读取配方制作恢复记录：{ex.Message}");
                state = new RecipeBrowserOrderState(characterId, null, ReadFailed: true);
            }

            return Cache[characterId] = state;
        }
    }

    public static bool TryCreate(RecipeBrowserOrder order)
        => TryUpdate(order.CharacterId, path =>
        {
            var temporaryPath = path + ".tmp";
            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(order)));
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }

            Cache[order.CharacterId] = new RecipeBrowserOrderState(order.CharacterId, order);
            return true;
        });

    public static bool TryRemove(ulong characterId, Guid? expectedId)
        => TryUpdate(characterId, path =>
        {
            var state = Read(characterId, path);
            if (state.Order is { } current && current.Id != expectedId || state.ReadFailed && expectedId.HasValue)
            {
                Cache[characterId] = state;
                return false;
            }

            File.Delete(path);
            Cache[characterId] = default;
            return true;
        });

    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    private static RecipeBrowserOrderState Read(ulong characterId, string path)
    {
        try
        {
            var order = JsonConvert.DeserializeObject<RecipeBrowserOrder>(File.ReadAllText(path));
            if (order is not { RecipeId: > 0, ItemId: > 0, Quantity: > 0, TargetCount: > 0 }
                || order.Id == Guid.Empty || order.CharacterId != characterId || string.IsNullOrWhiteSpace(order.Name)
                || order.Settings is { IngredientPreferences: null })
            {
                throw new JsonSerializationException("配方制作恢复记录缺少有效的角色、配方或库存目标。");
            }

            return new RecipeBrowserOrderState(characterId, order);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (JsonException ex)
        {
            GatherBuddy.Log.Warning($"[Vulcan] 无法解析配方制作恢复记录：{ex.Message}");
            return new RecipeBrowserOrderState(characterId, null, ReadFailed: true);
        }
    }

    private static string GetPath(ulong characterId)
        => Functions.ObtainSaveFile($"VulcanRecipeOrder-{characterId}.json")?.FullName
            ?? throw new IOException("无法访问插件存储目录。");

    private static bool TryUpdate(ulong characterId, Func<string, bool> update)
    {
        lock (CacheLock)
        {
            try
            {
                // Serialize file changes across game clients sharing this character's plugin configuration.
                using var mutex = new Mutex(false, $"GatherBuddy.VulcanRecipeOrder.{characterId}");
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        return false;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous client exited while holding the mutex.
                }

                try
                {
                    return update(GetPath(characterId));
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Cache.Remove(characterId);
                GatherBuddy.Log.Warning($"[Vulcan] 无法更新配方制作恢复记录：{ex.Message}");
                return false;
            }
        }
    }
}
