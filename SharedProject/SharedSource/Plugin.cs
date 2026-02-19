using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: IgnoresAccessChecksTo("Barotrauma")]
[assembly: IgnoresAccessChecksTo("BarotraumaCore")]
[assembly: IgnoresAccessChecksTo("DedicatedServer")]
namespace PutShitInOnePlace
{
    public partial class Plugin : IAssemblyPlugin
    {
        public static Harmony harmony;
        public static Plugin Instance { get; private set; }

        private Dictionary<Identifier, Dictionary<ushort, HashSet<ushort>>> cache = new();
        private Dictionary<ushort, (Identifier itemType, ushort containerId)> cacheAddressById = new();

        // Logging toggle
        private bool verboseLogging = false;

        public void Initialize()
        {
            Instance = this;

            harmony = new Harmony("com.chrisregner.putshitinoneplace");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            LuaCsLogger.Log("PutShitInOnePlace: Harmony patches applied");

            GameMain.LuaCs.Game.AddCommand(
                "psiop_logcontainers",
                "Logs container IDs and count. Example: psiop_logcontainers steel",
                (args) =>
                {
                    if (args.Length == 0)
                    {
                        LuaCsLogger.Log("Please provide an item identifier.");
                        return;
                    }

                    Identifier id = args[0].ToString().ToIdentifier();
                    if (cache.TryGetValue(id, out var containers))
                    {
                        LuaCsLogger.Log($"Containers for {id}:");
                        foreach (var kvp in containers)
                        {
                            LuaCsLogger.Log($"- Container ID: {kvp.Key}, Item Count: {kvp.Value.Count}");
                        }
                    }
                    else
                    {
                        LuaCsLogger.Log($"No cached containers found for {id}.");
                    }
                },
                null,
                false
            );

            GameMain.LuaCs.Game.AddCommand(
                "psiop_enablelogging",
                "Enables/Disables logging for debugging",
                (args) =>
                {
                    verboseLogging = !verboseLogging;
                    LuaCsLogger.Log($"PutShitInOnePlace: Verbose logging is now {(verboseLogging ? "ENABLED" : "DISABLED")}");
                },
                null,
                false
            );
        }

        #region Cache Logic

        private void AddToCache(Item item, Item container)
        {
            ushort itemId = item.ID;
            ushort containerId = container.ID;
            Identifier itemType = item.Prefab.Identifier;

            if (cacheAddressById.ContainsKey(itemId))
            {
                RemoveFromCache(item);
            }

            if (!cache.TryGetValue(itemType, out var containers))
            {
                containers = new Dictionary<ushort, HashSet<ushort>>();
                cache[itemType] = containers;
            }

            if (!containers.TryGetValue(containerId, out var itemIds))
            {
                itemIds = new HashSet<ushort>();
                containers[containerId] = itemIds;
            }

            itemIds.Add(itemId);
            cacheAddressById[itemId] = (itemType, containerId);

            if (verboseLogging)
                LuaCsLogger.Log($"PSIOP: Added Item {itemType} ({itemId}) to Container {containerId}.");
        }

        private void RemoveFromCache(Item item)
        {
            ushort itemId = item.ID;

            if (!cacheAddressById.TryGetValue(itemId, out var address))
                return;

            var (itemType, containerId) = address;

            if (cache.TryGetValue(itemType, out var containers))
            {
                if (containers.TryGetValue(containerId, out var itemIds))
                {
                    itemIds.Remove(itemId);
                    if (itemIds.Count == 0)
                        containers.Remove(containerId);
                }

                if (containers.Count == 0)
                    cache.Remove(itemType);
            }

            cacheAddressById.Remove(itemId);

            if (verboseLogging)
                LuaCsLogger.Log($"PSIOP: Removed Item {itemType} ({itemId}) from cache (Container {containerId}).");
        }

        #endregion

        [HarmonyPatch(typeof(HumanAIController))]
        [HarmonyPatch("FindSuitableContainer")]
        [HarmonyPatch(new Type[] { typeof(Character), typeof(Item), typeof(List<Item>), typeof(int), typeof(Item) },
                      new ArgumentType[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Out })]
        public class FindSuitableContainerPatch
        {
            [HarmonyPrefix]
            static void Prefix(ref int __state, ref int itemIndex)
            {
                __state = itemIndex;
            }

            [HarmonyPostfix]
            public static void Postfix(ref bool __result, ref int __state, Character character, Item containableItem, List<Item> ignoredItems, ref int itemIndex, ref Item suitableContainer)
            {
                string[] blacklistTags = {
                    "diving", "refillableoxygensource", "railgunammo",
                    "ammobox", "depthchargeammo", "crate"
                };

                if (blacklistTags.Any(tag => containableItem.HasTag(tag)))
                    return;

                var itemType = containableItem.Prefab.Identifier;
                var cache = Instance.cache;

                if (!cache.TryGetValue(itemType, out var containers))
                {
                    if (Instance.verboseLogging)
                        LuaCsLogger.Log($"PSIOP: No candidates in cache for {itemType}.");
                    return;
                }

                var sortedCandidates = containers.OrderByDescending(kvp => kvp.Value.Count).ToList();

                if (Instance.verboseLogging)
                    LuaCsLogger.Log($"PSIOP: Found {sortedCandidates.Count} potential container(s) for {itemType}.");

                foreach (var kvp in sortedCandidates)
                {
                    if (kvp.Key == 0) continue;
                    if (Entity.FindEntityByID(kvp.Key) is not Item targetItem)
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} not found in world.");
                        continue;
                    }

                    if (ignoredItems.Contains(targetItem))
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} is in ignored list.");
                        continue;
                    }

                    if (!targetItem.HasAccess(character))
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Character lacks access to {kvp.Key}.");
                        continue;
                    }

                    var containerComponent = targetItem.GetComponent<ItemContainer>();
                    if (containerComponent == null) continue;

                    if (targetItem.Submarine == null || !targetItem.Submarine.Info.IsPlayer)
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} is not in player submarine.");
                        continue;
                    }

                    if (!containerComponent.Inventory.CanBePut(containableItem))
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} is full or cannot accept item.");
                        continue;
                    }

                    Item root = targetItem.RootContainer ?? targetItem;
                    if (root.GetComponent<Fabricator>() != null || root.GetComponent<Deconstructor>() != null)
                    {
                        if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} is a machine/machine sub-container.");
                        continue;
                    }

                    if (character.AIController is HumanAIController humanAI)
                    {
                        var path = humanAI.PathSteering.PathFinder.FindPath(
                            character.SimPosition,
                            targetItem.SimPosition,
                            character.Submarine,
                            $"PutShitInOnePlace({character.DisplayName})",
                            0f, null, null,
                            node => node.Waypoint.CurrentHull != null);

                        if (path.Unreachable)
                        {
                            if (Instance.verboseLogging) LuaCsLogger.Log($"PSIOP: - Container {kvp.Key} is unreachable by pathing.");
                            ignoredItems.Add(targetItem);
                            continue;
                        }
                    }

                    // --- SUCCESS ---
                    if (Instance.verboseLogging)
                        LuaCsLogger.Log($"PSIOP: SUCCESS! Redirecting {character.DisplayName} to container {kvp.Key} (Count: {kvp.Value.Count}).");

                    suitableContainer = targetItem;
                    itemIndex = __state;
                    __result = true;

                    return;
                }
            }
        }

        [HarmonyPatch(typeof(Inventory), "PutItem")]
        public class PutItemPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Inventory __instance, Item item)
            {
                if (__instance.Owner is not Item container || container.Submarine?.Info.IsPlayer != true)
                    return;

                Instance.AddToCache(item, container);
            }
        }

        [HarmonyPatch(typeof(Inventory), "RemoveItem")]
        public class RemoveItemPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Inventory __instance, Item item)
            {
                if (__instance.Owner is not Item)
                    return;

                Instance.RemoveFromCache(item);
            }
        }

        //[HarmonyPatch(typeof(GameSession), "StartRound", typeof(LevelData), typeof(bool), typeof(SubmarineInfo), typeof(SubmarineInfo))]
        //public class StartRoundPatch
        //{
        //    [HarmonyPrefix]
        //    public static void Prefix() => Instance.ClearCache();
        //}

        [HarmonyPatch(typeof(GameSession), "EndRound")]
        public class EndRoundPatch
        {
            [HarmonyPrefix]
            public static void Prefix() => Instance.ClearCache();
        }

        public void ClearCache()
        {
            cache.Clear();
            cacheAddressById.Clear();
            LuaCsLogger.Log("PutShitInOnePlace: Cache cleared");
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            ClearCache();
        }
    }
}