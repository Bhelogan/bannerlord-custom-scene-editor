using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Editing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Spawns a practice skirmish on the current scene so the player can watch enemy AI
    /// pathfind across the navmesh. Modelled directly on BloodGames' BloodGamesMissionLogic
    /// and Homesteads' HomesteadSparringMissionLogic — both proven to produce fighting agents.
    ///
    /// The critical ingredients for agents that actually fight:
    ///   • MissionMode.Battle (set in AfterStart)
    ///   • AgentHumanAILogic in the mission behaviors (drives AI combat decisions)
    ///   • Agent.SetWatchState(Alarmed) on every combat agent
    ///   • Explicit team creation with SetIsEnemyOf in both directions
    ///   • Weapons wielded after a short delay (agents aren't fully ready in AfterStart)
    ///   • Charge orders issued every tick so both formations continuously pathfind toward each other
    /// </summary>
    internal class NavMeshTestBattleLogic : MissionLogic {
        private readonly Vec3 _playerPosition;
        private readonly Vec3 _enemyPosition;
        private readonly int _enemyCount;

        // Same basic composition as Homesteads Reloaded's tier-one Angry Mob: a small
        // mix of forest bandits, mountain bandits and looters, led by one boss when the
        // object database has one available. CSC has no homestead tier, so its test uses
        // this entry-level mix rather than pretending to know one.
        private static readonly string[] RaidCommonTroops = {
            "forest_bandits_bandit", "mountain_bandits_bandit", "looter"
        };
        private static readonly string[] RaidBossTroops = {
            "forest_bandits_boss", "mountain_bandits_boss", "desert_bandits_boss"
        };

        private float _timeSinceStart;
        private bool _weaponsWielded;
        private bool _spawned;
        private bool _spawnRouteLogged;

        public NavMeshTestBattleLogic(Vec3 playerPosition, Vec3 enemyPosition, int enemyCount) {
            _playerPosition = playerPosition;
            _enemyPosition = enemyPosition;
            // Preserve a raid-sized test without accidentally making a pathological 500-agent
            // mission from a late-game army. The scene test itself has no campaign consequences.
            _enemyCount = Math.Max(1, Math.Min(100, enemyCount));
        }

        public override void AfterStart() {
            base.AfterStart();
            try {
                Mission.SetMissionMode(MissionMode.Battle, atStart: true);
                InitializeTeams();
                SpawnPlayer();
                SpawnPlayerParty();
                SpawnEnemies();
                _spawned = true;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(NavMeshTestBattleLogic), "AfterStart failed", ex);
            }
        }

        public override void OnMissionTick(float dt) {
            base.OnMissionTick(dt);
            _timeSinceStart += dt;

            // Wield weapons after a short delay — agents aren't fully ready in AfterStart.
            // This mirrors BloodGamesMissionLogic and HomesteadSparringMissionLogic.
            if (!_weaponsWielded && _timeSinceStart > 0.5f) {
                foreach (Agent agent in Mission.Agents) {
                    if (!agent.IsHuman || agent.Team == null) continue;
                    agent.WieldInitialWeapons(Agent.WeaponWieldActionType.Instant);
                }
                _weaponsWielded = true;
            }

            // Both sides must charge in this test. Ordering only the raiders lets the player-side
            // formation choose to hold position and shoot through an obstacle, which looks like a
            // navmesh failure even when the attacking side has a route around it.
            if (_spawned && _weaponsWielded) {
                try {
                    if (!_spawnRouteLogged) LogSpawnRoute();
                    Agent? main = Mission.MainAgent;
                    if (main == null || !main.IsActive()) return;

                    SetChargeOrders(Mission.AttackerTeam);
                    SetChargeOrders(Mission.PlayerTeam);
                } catch (Exception ex) {
                    TraceLogger.WriteException(nameof(NavMeshTestBattleLogic), "Tick charge order failed", ex);
                }
            }
        }

        private static void SetChargeOrders(Team? team) {
            if (team == null) return;
            foreach (Formation formation in team.FormationsIncludingEmpty) {
                if (formation == null || formation.CountOfUnits == 0) continue;
                if (formation.GetReadonlyMovementOrderReference().OrderEnum
                    != MovementOrder.MovementOrderEnum.Charge) {
                    formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                }
            }
        }

        /// <summary>
        /// Records the same radius-aware route test that the editor inspector uses, but at the
        /// exact opposing team spawns. This separates a genuine missing detour from combat AI
        /// deciding to exchange missiles while a route exists.
        /// </summary>
        private void LogSpawnRoute() {
            _spawnRouteLogged = true;
            NavMeshProbe player = NavMeshDiagnostics.Probe(Mission.Scene, _playerPosition);
            NavMeshProbe enemy = NavMeshDiagnostics.Probe(Mission.Scene, _enemyPosition);
            NavMeshRoute route = NavMeshDiagnostics.TestRoute(Mission.Scene, player, enemy);
            TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                route.Reachable
                    ? $"Spawn route: reachable; direct={route.Direct}, redirect={route.RequiresRedirect}, " +
                      $"straight={route.StraightDistance:0.0}m, path={route.PathDistance:0.0}m, " +
                      $"detour={route.DetourRatio:0.00}x."
                    : $"Spawn route: NOT reachable — {route.Error}.");
        }

        /// <summary>
        /// Mission ends when one side is wiped out. Mirrors BloodGames' CheckDuelEnd.
        /// </summary>
        public override bool MissionEnded(ref MissionResult missionResult) {
            if (!_spawned || _timeSinceStart < 3f) return false;

            int playerCount = 0, enemyCount = 0;
            foreach (Agent agent in Mission.Agents) {
                if (!agent.IsHuman || agent.Health <= 0f || agent.Team == null) continue;
                if (agent.Team.Side == BattleSideEnum.Defender) playerCount++;
                else if (agent.Team.Side == BattleSideEnum.Attacker) enemyCount++;
            }

            if (enemyCount == 0) {
                missionResult = MissionResult.CreateSuccessful(Mission, false);
                return true;
            }
            if (playerCount == 0) {
                missionResult = MissionResult.CreateDefeated(Mission);
                return true;
            }
            return false;
        }

        public override InquiryData OnEndMissionRequest(out bool canPlayerLeave) {
            canPlayerLeave = true;
            return null;
        }

        // ── Team setup ───────────────────────────────────────────────────────────

        private void InitializeTeams() {
            // Do NOT use MissionBasicTeamLogic — it pre-creates banner-less teams before
            // AfterStart runs. Create teams ourselves with explicit enemy relations, exactly
            // as BloodGamesMissionLogic does.
            Banner? playerBanner = Clan.PlayerClan?.Banner;
            uint playerColor = Clan.PlayerClan?.Color ?? 0x0000FFU;
            uint playerColor2 = Clan.PlayerClan?.Color2 ?? 0xFFFFFFU;

            Team playerTeam = Mission.Teams.Add(
                BattleSideEnum.Defender, playerColor, playerColor2, playerBanner);
            Team enemyTeam = Mission.Teams.Add(
                BattleSideEnum.Attacker, 0xFF0000U, 0xFFFFFFU, null);

            playerTeam.SetIsEnemyOf(enemyTeam, true);
            enemyTeam.SetIsEnemyOf(playerTeam, true);

            Mission.PlayerTeam = playerTeam;
        }

        // ── Spawning ─────────────────────────────────────────────────────────────

        private void SpawnPlayer() {
            CharacterObject? character = ResolvePlayerCharacter(out string source);
            if (character == null) {
                TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                    "FATAL: no CharacterObject available for the player.");
                return;
            }

            Vec3 pos = SnapToGround(_playerPosition);
            Vec2 facing = DirectionToward(pos, _enemyPosition);

            Agent agent = SpawnAgent(character, pos, Mission.PlayerTeam, facing);
            agent.Controller = AgentControllerType.Player;
            Mission.MainAgent = agent;

            TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                $"Player spawned at ({pos.x:0.##}, {pos.y:0.##}, {pos.z:0.##}) via {source}; " +
                $"enemy target at ({_enemyPosition.x:0.##}, {_enemyPosition.y:0.##}).");
        }

        private void SpawnEnemies() {
            Vec3 pos = SnapToGround(_enemyPosition);
            Vec2 facing = DirectionToward(pos, _playerPosition);

            // Spawn in a loose line perpendicular to the player direction.
            Vec2 toPlayer = (_playerPosition.AsVec2 - pos.AsVec2);
            float len = toPlayer.Length;
            Vec2 forward = len > 0.001f ? toPlayer / len : new Vec2(0f, 1f);
            Vec2 side = new Vec2(-forward.y, forward.x);

            Team enemyTeam = Mission.AttackerTeam;
            float spacing = 1.5f;
            int half = _enemyCount / 2;

            for (int i = 0; i < _enemyCount; i++) {
                int offset = i - half;
                Vec3 spawnPos = pos + new Vec3(side.x * offset * spacing, side.y * offset * spacing, 0f);
                spawnPos = SnapToGround(spawnPos);

                CharacterObject? enemy = ResolveRaidEnemy(i);
                if (enemy == null) continue;
                SpawnAgent(enemy, spawnPos, enemyTeam, facing);
            }

            // Assign all enemies to a single infantry formation and let AI control it.
            Formation formation = enemyTeam.GetFormation(FormationClass.Infantry);
            formation?.SetControlledByAI(isControlledByAI: true);

            TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                $"Spawned up to {_enemyCount} raid-style enemies at ({pos.x:0.##}, {pos.y:0.##}).");
        }

        /// <summary>
        /// Adds healthy troops and companions from the current party to the defender side. This is
        /// a mission-only copy: a CSC test never changes the campaign roster.
        /// </summary>
        private void SpawnPlayerParty() {
            if (MobileParty.MainParty?.MemberRoster == null || Mission.PlayerTeam == null) return;

            var members = new List<CharacterObject>();
            try {
                foreach (TroopRosterElement element in MobileParty.MainParty.MemberRoster.GetTroopRoster()) {
                    CharacterObject? character = element.Character;
                    if (character == null || character.IsPlayerCharacter) continue;
                    int healthy = Math.Max(0, element.Number - element.WoundedNumber);
                    for (int n = 0; n < healthy && members.Count < 99; n++) members.Add(character);
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                    $"Could not read player party roster: {ex.GetType().Name}: {ex.Message}");
            }

            Vec3 center = SnapToGround(_playerPosition);
            Vec2 facing = DirectionToward(center, _enemyPosition);
            Vec2 toEnemy = _enemyPosition.AsVec2 - center.AsVec2;
            float length = toEnemy.Length;
            Vec2 forward = length > 0.001f ? toEnemy / length : new Vec2(0f, 1f);
            Vec2 side = new Vec2(-forward.y, forward.x);

            for (int i = 0; i < members.Count; i++) {
                int row = i / 8 + 1;
                int column = i % 8 - 4;
                Vec3 spawn = center + new Vec3(
                    side.x * column * 1.5f - forward.x * row * 1.8f,
                    side.y * column * 1.5f - forward.y * row * 1.8f, 0f);
                SpawnAgent(members[i], SnapToGround(spawn), Mission.PlayerTeam, facing);
            }

            foreach (Formation formation in Mission.PlayerTeam.FormationsIncludingEmpty) {
                if (formation != null && formation.CountOfUnits > 0) formation.SetControlledByAI(true);
            }
            TraceLogger.Write(nameof(NavMeshTestBattleLogic),
                $"Spawned {members.Count} healthy party member(s) beside the player for raid-scale test.");
        }

        // ── Shared spawn helper ───────────────────────────────────────────────────

        /// <summary>
        /// Spawns an agent with the same settings BloodGames uses: NoHorses, AI controller,
        /// Alarmed watch state. The player's controller is overridden by the caller.
        /// </summary>
        private Agent SpawnAgent(CharacterObject character, Vec3 position, Team team, Vec2 facing) {
            AgentBuildData buildData = new AgentBuildData(character)
                .Team(team)
                .InitialPosition(position)
                .InitialDirection(in facing)
                .NoHorses(true);

            Agent agent = Mission.SpawnAgent(buildData);
            agent.Controller = AgentControllerType.AI;
            // Alarmed is the watch state that makes agents enter combat-ready stance and
            // engage enemies. Without this they stand idle even in MissionMode.Battle.
            agent.SetWatchState(Agent.WatchState.Alarmed);
            return agent;
        }

        // ── Character resolution ──────────────────────────────────────────────────

        private static CharacterObject? ResolvePlayerCharacter(out string source) {
            source = "none";
            try {
                if (CharacterObject.PlayerCharacter != null) {
                    source = "PlayerCharacter";
                    return CharacterObject.PlayerCharacter;
                }
            } catch { }

            try {
                var all = Game.Current?.ObjectManager.GetObjectTypeList<CharacterObject>();
                if (all != null && all.Count > 0) {
                    CharacterObject? troop = all.FirstOrDefault(c =>
                        c.HeroObject == null && !c.IsTemplate && c.IsSoldier);
                    troop ??= all.FirstOrDefault(c => c.HeroObject == null && !c.IsTemplate);
                    if (troop != null) {
                        source = "ObjectManager fallback";
                        return troop;
                    }
                }
            } catch { }
            return null;
        }

        private static CharacterObject? ResolveEnemyCharacter() {
            try {
                var all = Game.Current?.ObjectManager.GetObjectTypeList<CharacterObject>();
                if (all == null) return null;

                // Prefer a basic infantry recruit — something that fights on foot and will
                // pathfind across the navmesh rather than riding a horse over it.
                CharacterObject? recruit = all.FirstOrDefault(c =>
                    c.HeroObject == null && !c.IsTemplate && c.IsSoldier
                    && !c.HasMount() && c.DefaultFormationClass == FormationClass.Infantry);
                recruit ??= all.FirstOrDefault(c =>
                    c.HeroObject == null && !c.IsTemplate && c.IsSoldier && !c.HasMount());
                recruit ??= all.FirstOrDefault(c => c.HeroObject == null && !c.IsTemplate && c.IsSoldier);
                return recruit;
            } catch {
                return null;
            }
        }

        private static CharacterObject? ResolveRaidEnemy(int index) {
            try {
                if (index == 0) {
                    CharacterObject? boss = CharacterObject.Find(RaidBossTroops[index % RaidBossTroops.Length]);
                    if (boss != null) return boss;
                }
                CharacterObject? troop = CharacterObject.Find(RaidCommonTroops[index % RaidCommonTroops.Length]);
                return troop ?? ResolveEnemyCharacter();
            } catch {
                return ResolveEnemyCharacter();
            }
        }

        // ── Position helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Snaps a world position to terrain height. Agents spawned at the terrain surface
        /// are on the navmesh; agents floating above or below it cannot pathfind.
        /// </summary>
        private Vec3 SnapToGround(Vec3 position) {
            try {
                float groundZ = Mission.Scene.GetGroundHeightAtPosition(position);
                if (groundZ < 9999f)
                    return new Vec3(position.x, position.y, groundZ + 0.1f);
            } catch { }
            return position;
        }

        private static Vec2 DirectionToward(Vec3 from, Vec3 to) {
            Vec2 delta = to.AsVec2 - from.AsVec2;
            float len = delta.Length;
            return len > 0.001f ? delta / len : new Vec2(0f, 1f);
        }

        // ── Position derivation from project data ─────────────────────────────────

        /// <summary>
        /// Derives player and enemy spawn positions from a project's navmesh cutout data.
        /// The player is placed on one side of the cutout cluster and enemies on the other,
        /// so the houses with cutout holes sit between them.
        /// </summary>
        public static (Vec3 playerPos, Vec3 enemyPos) DerivePositionsFromProject(SceneProject project) {
            if (project.NavMeshCutouts == null || project.NavMeshCutouts.Count == 0) {
                Vec3 fallback = Vec3.Zero;
                if (project.Entities.Count > 0) {
                    fallback = new Vec3(
                        project.Entities.Average(e => e.Pos[0]),
                        project.Entities.Average(e => e.Pos[1]),
                        project.Entities.Average(e => e.Pos[2]));
                }
                return (fallback, fallback + new Vec3(0f, 40f, 0f));
            }

            var allCorners = new List<Vec3>();
            foreach (var cutout in project.NavMeshCutouts) {
                Vec3[] corners = NavMeshCutoutAuthoring.Corners(cutout);
                allCorners.AddRange(corners);
            }

            if (allCorners.Count == 0) {
                return (Vec3.Zero, new Vec3(0f, 40f, 0f));
            }

            Vec3 center = new Vec3(
                allCorners.Average(c => c.x),
                allCorners.Average(c => c.y),
                allCorners.Average(c => c.z));

            float spreadX = allCorners.Max(c => c.x) - allCorners.Min(c => c.x);
            float spreadY = allCorners.Max(c => c.y) - allCorners.Min(c => c.y);
            // A routing test needs enough travel for agents to commit to the mesh rather than
            // simply see and fight the enemy through a wall. Keep the project obstacle cluster
            // between the teams, with at least 200 metres from player start to raider start.
            float offset = Math.Max(Math.Max(spreadX, spreadY) / 2f + 15f, 100f);

            Vec3 playerPos, enemyPos;
            if (spreadX >= spreadY) {
                playerPos = new Vec3(center.x - offset, center.y, center.z);
                enemyPos = new Vec3(center.x + offset, center.y, center.z);
            } else {
                playerPos = new Vec3(center.x, center.y - offset, center.z);
                enemyPos = new Vec3(center.x, center.y + offset, center.z);
            }

            return (playerPos, enemyPos);
        }

        /// <summary>Raid-side size mirrors the healthy people available in the current player party.</summary>
        public static int DeriveRaidEnemyCount() {
            try {
                int healthy = 0;
                if (MobileParty.MainParty?.MemberRoster != null) {
                    foreach (TroopRosterElement element in MobileParty.MainParty.MemberRoster.GetTroopRoster()) {
                        healthy += Math.Max(0, element.Number - element.WoundedNumber);
                    }
                }
                return Math.Max(1, healthy);
            } catch {
                return 1;
            }
        }
    }
}
