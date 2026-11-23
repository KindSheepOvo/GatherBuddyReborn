using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Automation;
using GatherBuddy.Classes;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Aetheryte = GatherBuddy.Classes.Aetheryte;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueDismount(bool moveAfterDismount = true)
        {
            TaskManager.Enqueue(StopNavigation);

            const uint DismountGeneralActionId = 23;
            var am = ActionManager.Instance();
            var actionType = moveAfterDismount ? ActionType.GeneralAction : ActionType.Mount;
            var actionId = moveAfterDismount ? DismountGeneralActionId : 0u;
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { am->UseAction(actionType, actionId); } }, "解除飞行");

            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.InFlight] && CanAct, 1000, "等待飞行状态取消");
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { am->UseAction(actionType, actionId); } }, "解除飞行 2");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Mounted] && CanAct, 1000, "等待坐骑状态取消");
            if (moveAfterDismount)
            {
                // A short movement pulse clears the suspended state after landing on the Chinese client.
                TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { Chat.ExecuteCommand("/automove on"); } }, "落地后短暂移动");
                TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { TaskManager.DelayNextImmediate(100); } });
                TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { Chat.ExecuteCommand("/automove off"); } }, "停止自动移动");
                TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { TaskManager.DelayNextImmediate(400); } });
            }
            else
            {
                // Fishing must stay at its selected cast position.
                TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Mounted]) { TaskManager.DelayNextImmediate(500); } });
            }
        }

        private unsafe void EnqueueMountUp()
        {
            const uint MountRouletteGeneralActionId = 9;
            var am = ActionManager.Instance();
            var mount = GatherBuddy.Config.AutoGatherConfig.AutoGatherMountId;
            Action doMount;

            if (IsMountUnlocked(mount) && am->GetActionStatus(ActionType.Mount, mount) == 0)
            {
                doMount = () => am->UseAction(ActionType.Mount, mount);
            }
            else
            {
                if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0)
                {
                    return;
                }

                doMount = () => am->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
            }

            EnqueueActionWithDelay(doMount);
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Mounted], 2000);
        }

        private unsafe bool CanMount()
        {
            var am = ActionManager.Instance();
            return am->GetActionStatus(ActionType.Mount, 0) == 0;
        }

        private unsafe bool IsMountUnlocked(uint mount)
        {
            var instance = PlayerState.Instance();
            if (instance == null)
            {
                return false;
            }

            return instance->IsMountUnlocked(mount);
        }

        private static bool WithinNodeInteractionRange(Vector3 position, Vector3 node)
            => Vector2.DistanceSquared(position.ToVector2(), node.ToVector2()) < 3.5f * 3.5f
            && Math.Abs(position.Y - node.Y) < 3f;

        private void MoveToCloseNode(IGameObject gameObject, Gatherable targetItem, ConfigPreset config)
        {
            if (!Player.Available)
            {
                return;
            }

            if (WithinNodeInteractionRange(Player.Position, gameObject.Position))
            {
                var waitGP = targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.CollectableMinGP;
                waitGP |= !targetItem.ItemData.IsCollectable && Player.Object.CurrentGp < config.GatherableMinGP;

                if (Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    EnqueueDismount();
                    TaskManager.Enqueue(() =>
                    {
                        if (Dalamud.Conditions[ConditionFlag.Mounted] && Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
                        {
                            ForceLandAndDismount();
                        }
                    });
                }
                else if (waitGP)
                {
                    StopNavigation();
                    AutoStatus = "等待采集 GP 恢复...";
                }
                else
                {
                    // Use consumables with cast time just before gathering a node when player is surely not mounted
                    if (GetConsumablesWithCastTime(config) is var consumable and > 0)
                    {
                        if (IsPathing)
                        {
                            StopNavigation();
                        }
                        else
                        {
                            EnqueueActionWithDelay(() => UseItem(consumable));
                        }
                    }
                    else
                    {
                        // Check perception requirement before interacting with node
                        if (DiscipleOfLand.Perception < targetItem.GatheringData.PerceptionReq)
                        {
                            Communicator.PrintError($"鉴别力不足无法采集该物品. 需要: {targetItem.GatheringData.PerceptionReq}, 当前: {DiscipleOfLand.Perception}");
                            AbortAutoGather();
                            return;
                        }

                        // If flying direct path to offset, complete navigation first, since offset is expected to be on the ground.
                        // Otherwise, stop once in range to interact.
                        if (!(_navState.offset && Dalamud.Conditions[ConditionFlag.InFlight] && IsPathing))
                        {
                            StopNavigation();
                            EnqueueNodeInteraction(gameObject, targetItem);
                        }
                        else
                        {
                            Navigate(gameObject.Position, false, nodeId: gameObject.BaseId);
                        }
                    }
                }
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position), nodeId: gameObject.BaseId);
            }
        }

        private void ForceLandAndDismount()
        {
            var floor = VNavmesh.Query.Mesh.NearestPoint(Player.Position, 5, 5);
            if (floor != null)
            {
                Navigate(floor.Value, true, direct: true);
                TaskManager.Enqueue(() => !IsPathGenerating);
                TaskManager.DelayNext(50);
                TaskManager.Enqueue(() => !IsPathing, 1000);
                EnqueueDismount();
            }
            // If even that fails, do advanced unstuck
            TaskManager.Enqueue(() => { if (Dalamud.Conditions[ConditionFlag.Mounted]) { _advancedUnstuck.Force(); } });
        }

        private void MoveToCloseSpearfishingNode(IGameObject gameObject, Classes.Fish targetFish, FishingSpot expectedSpot)
        {
            if (WithinNodeInteractionRange(Player.Position, gameObject.Position))
            {
                if (Dalamud.Conditions[ConditionFlag.Mounted])
                {
                    EnqueueDismount();
                }
                else
                {
                    EnqueueSpearfishingNodeInteraction(gameObject, targetFish, expectedSpot);
                }

                if (!Dalamud.Conditions[ConditionFlag.Diving])
                {
                    TaskManager.Enqueue(() => { if (!Dalamud.Conditions[ConditionFlag.Gathering]) { Navigate(gameObject.Position, false, nodeId: gameObject.BaseId); } });
                }
            }
            else if (Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2()) < Math.Max(GatherBuddy.Config.AutoGatherConfig.MountUpDistance, 5)
                && !Dalamud.Conditions[ConditionFlag.InFlight] && !Dalamud.Conditions[ConditionFlag.Diving])
            {
                Navigate(gameObject.Position, false, nodeId: gameObject.BaseId);
            }
            else
            {
                Navigate(gameObject.Position, ShouldFly(gameObject.Position), nodeId: gameObject.BaseId);
            }
        }

        private static bool NavigationMovementAllowed
            => !Dalamud.Conditions[ConditionFlag.Mounting] && !Dalamud.Conditions[ConditionFlag.Mounting71]
            && !Dalamud.Conditions[ConditionFlag.MountOrOrnamentTransition] && VNavmesh.Path.GetMovementAllowed();

        private void ResetNavigationProgress()
        {
            _advancedUnstuck.ResetTracking();
            _fishingApproachProgress.Reset();
            _waypointProgress.Reset();
        }

        private bool CheckActiveWaypoint(bool isPathing, bool isPathGenerating)
        {
            if (CurrentDestination == default || !isPathing
                || (isPathGenerating && _navState.stage != PathfindingStage.RetryCombinedPathfinding)
                || !NavigationMovementAllowed)
            {
                _waypointProgress.Reset();
                return false;
            }

            var now = Environment.TickCount64;
            if (!_waypointProgress.ShouldSample(now))
                return false;

            var remaining = VNavmesh.Path.ListWaypoints();
            if (remaining.Count == 0)
            {
                _waypointProgress.Reset();
                return false;
            }

            var player = Player.Position;
            var waypoint = remaining[0];
            var distance = _navState.flying && !_navState.mountingUp
                ? Vector3.Distance(player, waypoint)
                : Vector2.Distance(player.ToVector2(), waypoint.ToVector2());
            var timeout = Math.Max(8f, 3 * GatherBuddy.Config.AutoGatherConfig.NavResetThreshold);
            var stalled = _waypointProgress.IsStalled(waypoint, distance, now, timeout);
            if (!stalled && Vector3.DistanceSquared(remaining[^1], _navState.pathDestination) < 1f)
                return false;

            var reason = stalled ? "未接近当前路径点" : "执行中的路径终点不匹配";
            GatherBuddy.Log.Warning($"{reason}，角色={player}，下一路径点={waypoint}，剩余点数={remaining.Count}，实际终点={remaining[^1]}，预期终点={_navState.pathDestination}");
            if (_fishingApproachTarget is { } fish && CurrentDestination == _fishingApproachPosition)
                FailFishingApproach(fish, reason, "正在寻找可抵达的钓点...", true, false);
            else
            {
                StopNavigation();
                AutoStatus = "正在从当前位置重新寻路...";
            }

            return true;
        }

        private void StopNavigation()
        {
            _fishingApproachPathObserved = false;
            StopPathfinding();

            _navState = default;
            if (VNavmesh.Enabled)
            {
                VNavmesh.Path.Stop();
            }
        }

        private void StopPathfinding()
        {
            ResetNavigationProgress();
            if (_navState.cts != null && _navState.task != null)
            {
                var cts = _navState.cts;
                cts.Cancel();
                _navState.task.ContinueWith(_ => cts.Dispose());
                _navState.task = null;
                _navState.cts = null;
            }
        }

        private unsafe void SetRotation(Angle angle)
        {
            if (!Player.Available)
            {
                return;
            }

            var playerObject = (GameObject*)Player.Object.Address;
            GatherBuddy.Log.Debug($"设置朝向角度为 {angle.Rad}");
            playerObject->SetRotation(angle.Rad);
        }

        private void Navigate(Vector3 destination, bool shouldFly, bool direct = false, uint? nodeId = null)
        {
            var canMount = Vector2.Distance(destination.ToVector2(), Player.Position.ToVector2()) >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance && CanMount();
            if (!Dalamud.Conditions[ConditionFlag.Mounted] && canMount)
            {
                EnqueueMountUp();
                if (!GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting)
                {
                    StopNavigation();
                    return;
                }
            }

            var landingDistance = GatherBuddy.Config.AutoGatherConfig.LandingDistance;

            if (_navState.destination == destination && _navState.stage == PathfindingStage.RetryCombinedPathfinding
                && _navState.task == null && Environment.TickCount64 - _navState.lastTry > 1000)
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.cts = new CancellationTokenSource();
                _navState.retryStart = Player.Position;
                _navState.task = FindCombinedPath(_navState.retryStart, destination, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _navState.cts.Token);
                GatherBuddy.Log.Debug($"重新尝试组合寻路到 {destination}");
                return;
            }

            if (_navState.destination == destination && (IsPathing || _navState.task != null))
            {
                return;
            }

            StopNavigation();

            shouldFly &= canMount || Dalamud.Conditions[ConditionFlag.Mounted];
            shouldFly |= Dalamud.Conditions[ConditionFlag.Diving];

            var offsettedDestination = GetCorrectedDestination(destination, Player.Position, nodeId);
            _navState.destination = destination;
            _navState.pathDestination = offsettedDestination;
            _navState.flying = shouldFly;
            _navState.mountingUp = shouldFly && !Dalamud.Conditions[ConditionFlag.Mounted] && !Dalamud.Conditions[ConditionFlag.Diving];
            _navState.direct = direct || !shouldFly || landingDistance == 0 || destination != offsettedDestination || Dalamud.Conditions[ConditionFlag.Diving];
            _navState.offset = destination != offsettedDestination;
            StartPathfinding(landingDistance);
        }

        private void StartPathfinding(float landingDistance)
        {
            _navState.cts = new CancellationTokenSource();
            _navState.stage = PathfindingStage.InitialCombinedPathfinding;
            _navState.retriedDirect = false;
            if (_navState.direct)
            {
                _navState.task = FindDirectPath(Player.Position, _navState.pathDestination, _navState.flying, _navState.cts.Token);
            }
            else
            {
                _navState.lastTry = Environment.TickCount64;
                _navState.task = FindCombinedPath(Player.Position, _navState.pathDestination, landingDistance, Dalamud.Conditions[ConditionFlag.InFlight], _navState.cts.Token);
            }
        }

        private void HandlePathfinding()
        {
            if (_navState.destination == default)
            {
                return;
            }

            var player = Player.Position;
            if (_navState.mountingUp && Dalamud.Conditions[ConditionFlag.Mounted])
            {
                _navState.mountingUp = false;
                if (_navState.task == null || IsPathing)
                {
                    // Ground movement ignores height and may already have consumed an ascent waypoint.
                    VNavmesh.Path.Stop();
                    StopPathfinding();
                    StartPathfinding(GatherBuddy.Config.AutoGatherConfig.LandingDistance);
                    return;
                }
            }

            if (_navState.flying && !_navState.mountingUp && _navState.stage == PathfindingStage.Done
                && !Dalamud.Conditions[ConditionFlag.Diving]
                && VNavmesh.Path.NumWaypoints() <= _navState.groundWaypointCount)
            {
                List<Vector3> remaining = [.. VNavmesh.Path.ListWaypoints()];
                _navState.flying = false;
                _waypointProgress.Reset();
                if (remaining.Count > 0)
                {
                    if (Vector3.DistanceSquared(remaining[^1], _navState.pathDestination) >= 1f)
                    {
                        StopNavigation();
                        return;
                    }

                    VNavmesh.Path.Stop();
                    VNavmesh.Path.MoveTo(remaining, false);
                    Dismount();
                }
                return;
            }

            if (_navState.task == null || _navState.cts == null || !_navState.task.IsCompleted)
            {
                return;
            }

            NavigationPath? route;
            try
            {
                route = _navState.task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                StopNavigation();
                return;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"寻路任务抛出异常: {ex.Message}");
                route = null;
            }
            _navState.cts.Dispose();
            _navState.task = null;
            _navState.cts = null;

            if (route != null && _navState.offset && !WithinNodeInteractionRange(route.Waypoints[^1], _navState.destination))
            {
                GatherBuddy.Log.Warning($"路径终点 {route.Waypoints[^1]} 超出采集点 {_navState.destination} 的交互范围，偏移目标={_navState.pathDestination}");
                route = null;
            }

            if (route == null)
            {
                if (_navState.direct || _navState.stage == PathfindingStage.FallbackDirectPathfinding)
                {
                    if (!_navState.retriedDirect)
                    {
                        _navState.retriedDirect = true;
                        _navState.cts = new CancellationTokenSource();
                        _navState.task = FindDirectPath(player, _navState.pathDestination, _navState.flying, _navState.cts.Token);
                        GatherBuddy.Log.Debug($"直接寻路失败，从当前位置重新计算到 {_navState.pathDestination} 的路径");
                        return;
                    }

                    GatherBuddy.Log.Error($"VNavmesh 重试后仍找不到前往 {_navState.pathDestination} 的路径");
                    StopNavigation();
                    _advancedUnstuck.Force();
                }
                else if (_navState.stage == PathfindingStage.InitialCombinedPathfinding)
                {
                    GatherBuddy.Log.Debug($"VNavmesh 未能找到组合路径，回退到直接路径");
                    _navState.stage++;
                    _navState.cts = new CancellationTokenSource();
                    _navState.task = FindDirectPath(player, _navState.destination, _navState.flying, _navState.cts.Token);
                }
                else if (_navState.stage == PathfindingStage.RetryCombinedPathfinding)
                {
                    if (!IsPathing)
                        StopNavigation();
                }
                else
                {
                    GatherBuddy.Log.Error($"BUG: 在非预期阶段 {_navState.stage} 寻路失败");
                    AbortAutoGather();
                }
            }
            else
            {
                var path = route.Waypoints;
                if (_navState.stage == PathfindingStage.RetryCombinedPathfinding
                    && !TryJoinFlightLeg(path, route.GroundWaypointCount, _navState.retryStart, player))
                {
                    // Keep the current fallback only while it is still running.
                    if (!IsPathing)
                        StopNavigation();
                    return;
                }

                if (route.GroundWaypointCount > 0)
                {
                    _navState.stage = PathfindingStage.Done;
                    _navState.groundWaypointCount = route.GroundWaypointCount;
                }
                else if (_navState.stage == PathfindingStage.FallbackDirectPathfinding)
                {
                    _navState.stage = PathfindingStage.RetryCombinedPathfinding;
                }

                VNavmesh.Path.Stop();
                VNavmesh.Path.MoveTo(path, _navState.flying && !_navState.mountingUp);
                GatherBuddy.Log.Debug($"开始导航，目标={_navState.destination}，寻路目标={_navState.pathDestination}，路径终点={path[^1]}，路径点={path.Count}");
            }
        }

        private static bool TryJoinFlightLeg(List<Vector3> path, int groundCount, Vector3 start, Vector3 player)
        {
            // Only skip points when the player is actually on a segment of the replacement route.
            for (var i = 0; i < path.Count - groundCount; ++i)
            {
                var end = path[i];
                var delta = end - start;
                var lengthSquared = delta.LengthSquared();
                var t = lengthSquared == 0 ? 0 : Math.Clamp(Vector3.Dot(player - start, delta) / lengthSquared, 0f, 1f);
                if (Vector3.DistanceSquared(player, start + t * delta) <= 1f)
                {
                    path.RemoveRange(0, i);
                    return true;
                }

                start = end;
            }

            return false;
        }

        private unsafe void Dismount()
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23); // Hotkey Z
        }

        private static async Task<List<Vector3>> FindWaypoints(Vector3 start, Vector3 target, bool fly, CancellationToken token)
        {
            var path = await VNavmesh.Nav.PathfindCancelable(start, target, fly, token);
            if (path.Count > 0 && (!path.TrueForAll(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))
                || Vector3.DistanceSquared(path[^1], target) >= 1f))
            {
                GatherBuddy.Log.Warning($"拒绝无效寻路结果，起点={start}，目标={target}，实际终点={path[^1]}，飞行={fly}");
                return [];
            }

            return path;
        }

        private static async Task<NavigationPath?> FindDirectPath(Vector3 start, Vector3 target, bool fly, CancellationToken token)
        {
            var path = await FindWaypoints(start, target, fly, token);
            return path.Count == 0 ? null : new NavigationPath(path);
        }

        private static async Task<NavigationPath?> FindCombinedPath(Vector3 player, Vector3 target, float landingDistance, bool flying, CancellationToken token)
        {
            var floor = flying ? VNavmesh.Query.Mesh.PointOnFloor(player, false, 5f) : player;
            if (floor == null)
                return null;

            var ground = await FindWaypoints(floor.Value, target, false, token);
            if (ground.Count < 2)
                return null;

            var landing = FindLandingPoint(ground, target, landingDistance);
            var meshPoint = VNavmesh.Query.Mesh.NearestPoint(landing, landingDistance, 10f);
            if (meshPoint == null || Math.Abs(target.Y - meshPoint.Value.Y) > 10f)
                return null;

            var flight = await FindWaypoints(player, meshPoint.Value, true, token);
            if (flight.Count == 0)
                return null;

            // Snapping can put the landing point across a barrier from the original ground segment.
            // Ask for a connected ground leg starting at the actual end of the flight.
            var approach = await FindWaypoints(flight[^1], target, false, token);
            if (approach.Count == 0)
                return null;

            flight.AddRange(approach);
            return new NavigationPath(flight, approach.Count);
        }

        private static Vector3 FindLandingPoint(List<Vector3> ground, Vector3 target, float radius)
        {
            var radiusSquared = radius * radius;
            for (var i = ground.Count - 2; i >= 0; --i)
            {
                var start = ground[i];
                var end = ground[i + 1];
                if (Vector3.DistanceSquared(start, target) <= radiusSquared)
                    continue;

                // Select the point on this segment entering the landing sphere.
                var delta = end - start;
                var lengthSquared = delta.LengthSquared();
                if (lengthSquared == 0 || Vector3.DistanceSquared(end, target) > radiusSquared)
                    return end;

                var fromCenter = start - target;
                var b = Vector3.Dot(fromCenter, delta);
                var c = fromCenter.LengthSquared() - radiusSquared;
                var t = (-b - MathF.Sqrt(MathF.Max(0, b * b - lengthSquared * c))) / lengthSquared;
                return Vector3.Lerp(start, end, Math.Clamp(t, 0f, 1f));
            }

            return ground[0];
        }

        private static Vector3 GetCorrectedDestination(in Vector3 destination, in Vector3 player, uint? nodeId)
        {
            const float MaxHorizontalSeparation = 3.0f;
            const float MaxVerticalSeparation = 2.5f;

            if (!GatherBuddy.Config.AutoGatherConfig.DisableRandomLandingPositions
                && nodeId.HasValue
                && AutoOffsets.TryGetRandomOffset(nodeId.Value, destination, player, out var offset))
            {
                GatherBuddy.Log.Debug($"为采集点 {nodeId.Value} 使用自动偏移: {offset}。到采集点距离: {Vector2.Distance(offset.ToVector2(), destination.ToVector2()):F2}y，角度: {Math.Acos(Vector2.Dot(Vector2.Normalize((player - destination).ToVector2()), Vector2.Normalize((offset - destination).ToVector2()))) * 180.0 / Math.PI:F1}度");
                return offset;
            }

            try
            {
                float separation;
                if (WorldData.NodeOffsets.TryGetValue(destination, out offset))
                {
                    offset = VNavmesh.Query.Mesh.NearestPoint(offset, MaxHorizontalSeparation, MaxVerticalSeparation).GetValueOrDefault(offset);
                    if ((separation = Vector2.Distance(offset.ToVector2(), destination.ToVector2())) > MaxHorizontalSeparation)
                    {
                        GatherBuddy.Log.Warning($"偏移被忽略，校正网格后水平间距 {separation} 过大。允许的最大值为 {MaxHorizontalSeparation}");
                    }
                    else if ((separation = Math.Abs(offset.Y - destination.Y)) > MaxVerticalSeparation)
                    {
                        GatherBuddy.Log.Warning($"偏移被忽略，校正网格后垂直间距 {separation} 过大。允许的最大值为 {MaxVerticalSeparation}");
                    }
                    else
                    {
                        return offset;
                    }
                }

                // There was code that corrected the destination to the nearest point on the mesh, but testing showed that
                // navigating directly to the node yields better results, and points within landing distance are on the mesh anyway.
            }
            catch (Exception) { }

            return destination;
        }

        private unsafe void MoveToFishingSpot(Vector3 position, Angle angle)
        {
            Navigate(position, ShouldFly(position));
        }

        public static Aetheryte? FindClosestAetheryte(ILocation location)
        {
            var aetheryte = location.ClosestAetheryte;

            var territory = location.Territory;
            if (ForcedAetherytes.ZonesWithoutAetherytes.FirstOrDefault(x => x.ZoneId == territory.Id).AetheryteId is var alt && alt > 0)
            {
                territory = GatherBuddy.GameData.Aetherytes[alt].Territory;
            }

            if (aetheryte == null || !Teleporter.IsAttuned(aetheryte.Id) || aetheryte.Territory != territory)
            {
                aetheryte = territory.Aetherytes
                    .Where(a => Teleporter.IsAttuned(a.Id))
                    .OrderBy(a => a.WorldDistance(territory.Id, location.IntegralXCoord, location.IntegralYCoord))
                    .FirstOrDefault();
            }

            return aetheryte;
        }

        private bool MoveToTerritory(ILocation location)
        {
            var aetheryte = FindClosestAetheryte(location);
            if (aetheryte == null)
            {
                Communicator.PrintError("无法找到可用的以太之光来进行传送。");
                return false;
            }

            GatherBuddy.Log.Debug($"[MoveToTerritory] 传送到 {aetheryte.Name}");
            TaskManager.Enqueue(() => Teleporter.Teleport(aetheryte.Id) ? true : (bool?)null, $"传送至 {aetheryte.Name}");
            // A transfer can finish between frames. The aetheryte may also be in a gateway zone.
            TaskManager.Enqueue(() => Dalamud.ClientState.TerritoryType == aetheryte.Territory.Id
                                   && CanAct && GenericHelpers.IsScreenReady(),
                30000, abortOnTimeout: true, name: $"等待抵达 {aetheryte.Name}");
            TaskManager.DelayNext((int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay);

            return true;
        }
    }
}
