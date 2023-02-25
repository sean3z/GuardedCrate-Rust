using System.Collections.Generic;
using System.Collections;
using System;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using UnityEngine.AI;
using VLB;
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
    [Info("Guarded Crate Modded", "Bazz3l", "1.7.1")]
    [Description("Spawns hackable crate events at random locations guarded by scientists.")]
    public class GuardedCrateMod : CovalencePlugin
    {
        [PluginReference] Plugin HackableLock, Clans, Kits;

        #region Fields

        private const string USE_PERM = "guardedcrate.use";

        private const string CRATE_PREFAB = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
        private const string MARKER_PREFAB = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string CHUTE_PREFAB = "assets/prefabs/misc/parachute/parachute.prefab";
        private const string PLANE_PREFAB = "assets/prefabs/npc/cargo plane/cargo_plane.prefab";
        private const string BRADLEY_PREFAB = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";

        private static readonly string[] NPC_PREFABS = {
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab",
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab",
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_oilrig.prefab",
            "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_patrol.prefab"
        };

        private readonly int _layers =  Layers.Terrain | Layers.World | Layers.Construction | Layers.Deploy;
        private readonly Dictionary<BaseEntity, CrateEvent> _entities = new Dictionary<BaseEntity, CrateEvent>();
        private readonly HashSet<CrateEvent> _events = new HashSet<CrateEvent>();
        private readonly List<Monument> _monuments = new List<Monument>();
        private readonly SpawnFilter _filter = new SpawnFilter();
        private static PluginConfig _config;
        private PluginData _stored;

        #endregion

        #region Config

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new JsonException();

                if (_config.ToDictionary().Keys.SequenceEqual(Config.ToDictionary(x => x.Key, x => x.Value).Keys)) return;

                PrintWarning("Loaded updated config.");

                SaveConfig();
            }
            catch
            {
                PrintError(
                    "The configuration file contains an error and has been replaced with a default config.\n" +
                    "The error configuration file was saved in the .jsonError extension");

                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);
        
        private class PluginConfig
        {
            [JsonProperty("EnableAutoStart (enables events to spawn automatically)")]
            public bool EnableAutoStart = false;

            [JsonProperty("EventDuration (time between event spawns)")]
            public float EventDuration = 1800f;
            
            [JsonProperty("EnableNotifications (enables events notifications)")]
            public bool EnableNotifications = true;
            
            [JsonProperty("Spawn distance from bases")]
            public float SpawnDistanceFromBases = 30f;

            [JsonProperty("Command (command name)")]
            public string[] Command = { "gcm" };

            [JsonProperty("EffectiveWeaponRange (range weapons will be effective)")]
            public Dictionary<string, float> EffectiveWeaponRange = new Dictionary<string, float>
            {
                { "snowballgun", 60f },
                { "rifle.ak", 150f },
                { "rifle.bolt", 150f },
                { "bow.hunting", 30f },
                { "bow.compound", 30f },
                { "crossbow", 30f },
                { "shotgun.double", 10f },
                { "pistol.eoka", 10f },
                { "multiplegrenadelauncher", 50f },
                { "rifle.l96", 150f },
                { "rifle.lr300", 150f },
                { "lmg.m249", 150f },
                { "rifle.m39", 150f },
                { "pistol.m92", 15f },
                { "smg.mp5", 80f },
                { "pistol.nailgun", 10f },
                { "shotgun.waterpipe", 10f },
                { "pistol.python", 60f },
                { "pistol.revolver", 50f },
                { "rocket.launcher", 60f },
                { "shotgun.pump", 10f },
                { "pistol.semiauto", 30f },
                { "rifle.semiauto", 100f },
                { "smg.2", 80f },
                { "shotgun.spas12", 30f },
                { "speargun", 10f },
                { "smg.thompson", 30f }
            };

            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonConvert.DeserializeObject<Dictionary<string, object>>(ToJson());
        }

        #endregion

        #region Storage

        private class PluginData
        {
            public List<EventSetting> Events = new List<EventSetting>();

            private Dictionary<string, EventSetting> _allSettings;

            [JsonIgnore]
            public Dictionary<string, EventSetting> AllSettings
            {
                get
                {
                    if (_allSettings == null)
                    {
                        _allSettings = new Dictionary<string, EventSetting>(StringComparer.OrdinalIgnoreCase);

                        foreach (var eventSetting in Events)
                            _allSettings[eventSetting.EventName] = eventSetting;
                    }

                    return _allSettings;
                }
            }

            public static PluginData LoadData() => Interface.Oxide.DataFileSystem.ReadObject<PluginData>("GuardedCrateMod") ?? new PluginData();

            public void Save() => Interface.Oxide.DataFileSystem.WriteObject("GuardedCrateMod", this);

            public EventSetting FindEvent(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                
                EventSetting settings;
                return AllSettings.TryGetValue(name, out settings) ? settings : null;
            }
        }

        private class EventSetting
        {
            [JsonProperty("EventDuration (duration the event will be active for)")]
            public float EventDuration;

            [JsonProperty("EventName (event name)")]
            public string EventName;

            [JsonProperty("AutoHack (enables auto hacking of crates when an event is finished)")]
            public bool AutoHack = true;

            [JsonProperty("AutoHackSeconds (countdown for crate to unlock in seconds)")]
            public float AutoHackSeconds = 60f;

            [JsonProperty("LockToPlayer (locks the crate to a player finishing the event)")]
            public bool LockToPlayer = false;

            [JsonProperty("DisplayClan (display clan tag rather than player name)")]
            public bool DisplayClan = true;

            [JsonProperty("UseKits (use custom kits plugin)")]
            public bool UseKits = false;

            [JsonProperty("Kits (custom kits)")]
            public List<string> Kits = new List<string>();

            [JsonProperty("NpcName (custom name)")]
            public string NpcName;

            [JsonProperty("NpcCount (number of guards to spawn)")]
            public int NpcCount;

            [JsonProperty("NpcHealth (health guards spawn with)")]
            public float NpcHealth;

            [JsonProperty("NpcRoamRadius (npc roam radius)")]
            public float NpcRoamRadius = 10f;

            [JsonProperty("NpcLookRadius (npc look radius)")]
            public float NpcLookRadius = 100f;

            [JsonProperty("NpcAttackRadius (npc attack radius)")]
            public float NpcAttackRadius = 150f;

            [JsonProperty("CargoSpeed (time cargo plane will drop in seconds)")]
            public float CargoSpeed = 120f;

            [JsonProperty("MarkerColor (marker color)")]
            public string MarkerColor;

            [JsonProperty("MarkerBorderColor (marker border color)")]
            public string MarkerBorderColor;

            [JsonProperty("MarkerOpacity (marker opacity)")]
            public float MarkerOpacity = 1f;

            [JsonProperty("UseLoot (use custom loot table)")]
            public bool UseLoot = false;

            [JsonProperty("MaxLootItems (max items to spawn in crate)")]
            public int MaxLootItems = 6;

            [JsonProperty("CustomLoot (items to spawn in crate)")]
            public List<LootItem> CustomLoot = new List<LootItem>();
        }

        private class LootItem
        {
            public string Shortname;
            public int MinAmount;
            public int MaxAmount;
            public ulong SkinID = 0UL;
            public string DisplayName = "";

            public Item CreateItem()
            {
                var item =  ItemManager.CreateByName(Shortname, Random.Range(MinAmount, MaxAmount), SkinID);
                if (item == null) 
                    return null;
                
                if (!string.IsNullOrEmpty(DisplayName)) 
                    item.name = DisplayName;

                return item;
            }
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "InvalidSyntax", "/gcm start <name>\n/gcm stop\n/gcm me <name>" },
                { "Prefix", "<color=#5AB5C7>Guarded Crate</color>" },
                { "Permission", "No permission" },
                { "CreateEvent", "New event starting stand by." },
                { "CleanEvents", "Cleaning up events." },
                {
                    "EventStarted",
                    "<color=#EDDf45>{0}</color>, event started at <color=#EDDf45>{1}</color>, eliminate the guards before they leave in <color=#EDDf45>{2}</color>."
                },
                {
                    "EventEnded",
                    "The event ended at the location <color=#EDDf45>{0}</color>, <color=#EDDf45>{1}</color> cleared the event!"
                },
                {
                    "EventClear",
                    "The event ended at <color=#EDDf45>{0}</color>; You were not fast enough; better luck next time!"
                },
            }, this);
        }

        #endregion

        #region Oxide

        private void OnServerInitialized()
        {
            AddCovalenceCommand(_config.Command, nameof(GCCommand), USE_PERM);

            LoadMonuments();
            LoadDefaults();

            if (_config.EnableAutoStart)
                timer.Every(_config.EventDuration, () => StartEvent());

            timer.Every(30f, RefreshEvents);
        }

        private void Init()
        {
            _stored = PluginData.LoadData();
        }

        private void Unload()
        {
            StopEvents();

            _config = null;
        }

        private void OnEntityDeath(ScientistNPC npc, HitInfo hitInfo) =>
            FindEntityEvent(npc)
                ?.OnNPCDeath(npc, hitInfo?.InitiatorPlayer);

        private void OnEntityKill(ScientistNPC npc) =>
            FindEntityEvent(npc)
                ?.OnNPCDeath(npc, null);

        private object OnNpcTarget(ScientistNPC npc, BasePlayer player)
        {
            if (FindEntityEvent(npc) == null)
                return null;

            if (player != null && (player.IsNpc || !player.userID.IsSteamId()))
                return true;

            return null;
        }

        private void OnCrateLanded(HackableLockedCrate crate) =>
            FindEntityEvent(crate)
                ?.StartRoutine();

        private object CanHackCrate(BasePlayer player, HackableLockedCrate crate) =>
            FindEntityEvent(crate)
                ?.OnCanHackCrate();

        private object CanPopulateLoot(HackableLockedCrate crate)
        {
            if (FindEntityEvent(crate) != null) return false;
            
            return null;
        }

        private object OnNpcRustEdit(ScientistNPC npc)
        {
            if (npc != null && FindEntityEvent(npc) != null) return true;
            
            return null;
        }

        #endregion

        #region Core

        #region Defaults

        private void LoadDefaults()
        {
            if (_stored.Events == null || _stored.Events.Count != 0) return;

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 800f,
                EventName = "Easy",
                NpcCount = 6,
                NpcHealth = 100,
                NpcName = "Easy Guard",
                MarkerColor = "#32a844",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 800f,
                EventName = "Medium",
                NpcCount = 8,
                NpcHealth = 150,
                NpcName = "Medium Guard",
                MarkerColor = "#eddf45",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 1800f,
                EventName = "Hard",
                NpcCount = 10,
                NpcHealth = 200,
                NpcName = "Hard Guard",
                MarkerColor = "#3060d9",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Events.Add(new EventSetting
            {
                EventDuration = 1800f,
                EventName = "Elite",
                NpcCount = 12,
                NpcHealth = 350,
                NpcName = "Elite Guard",
                MarkerColor = "#e81728",
                MarkerBorderColor = "#000000",
                MarkerOpacity = 0.9f
            });

            _stored.Save();
        }

        #endregion

        #region Management

        private void StartEvent(string name = null)
        {
            var settings = _stored.FindEvent(name);
            new CrateEvent(this).SetupEvent(settings ?? _stored.Events.GetRandom());
        }

        private void StartEventOnPlayer(IPlayer player, string name = null)
        {
            var settings = _stored.FindEvent(name);
            new CrateEvent(this).SetupEventOnPlayer(settings ?? _stored.Events.GetRandom(), player);
        }

        private void StopEvents() => CommunityEntity.ServerInstance.StartCoroutine(DespawnRoutine());

        private void RefreshEvents()
        {
            for (var i = 0; i < _events.Count; i++)
                _events.ElementAt(i)
                    ?.RefreshEvent();
        }

        #endregion

        #region Cleanup

        private IEnumerator DespawnRoutine()
        {
            for (var i = _events.Count - 1; i >= 0; i--)
            {
                _events.ElementAt(i)
                    ?.StopEvent();
                
                yield return CoroutineEx.waitForSeconds(0.25f);
            }
        }

        #endregion

        #region Cache

        private CrateEvent FindEntityEvent(BaseEntity entity) => _entities.GetValueOrDefault(entity);

        private void AddEntity(BaseEntity entity, CrateEvent crateEvent) => _entities.Add(entity, crateEvent);

        private void DelEntity(BaseEntity entity) => _entities.Remove(entity);

        private void AddEvent(CrateEvent crateEvent) => _events.Add(crateEvent);

        private void DelEvent(CrateEvent crateEvent) => _events.Remove(crateEvent);

        #endregion

        #region Position

        private Vector3 GetPosition()
        {
            Vector3 vector;

            var num = 100f;

            do
            {
                vector = IsValidEventPosition(FindRandomPosition());
            } while (vector == Vector3.zero && --num > 0f);

            return vector;
        }

        private Vector3 GetPlayerPosition(IPlayer player)
        {
            BasePlayer _player = player.Object as BasePlayer;
            
            // if (!IsValidEventPosition(_player.transform.position))
            // {
            //     // TODO: spawn some where _near_ the player instead of random
            //     return GetPosition();
            // }

            return _player.transform.position; 
        }

        private Vector3 FindRandomPosition()
        {
            Vector3 vector;
            
            var x = TerrainMeta.Size.x / 2f;
            var num = 100f;

            do
            {
                vector = Vector3Ex.Range(-x, x);
            } while (_filter.GetFactor(vector) == 0f && --num > 0f);

            vector.y = 0f;

            return vector;
        }

        private Vector3 IsValidEventPosition(Vector3 position)
        {
            RaycastHit hit;

            position.y += 250f;
            
            if (!Physics.Raycast(position, Vector3.down, out hit, position.y + 10f, _layers, QueryTriggerInteraction.Ignore)) 
                return Vector3.zero;
            
            position.y = Mathf.Max(hit.point.y, TerrainMeta.HeightMap.GetHeight(position));

            if (WaterLevel.GetWaterDepth(position) > 0.1f || WaterLevel.Test(position)) 
                return Vector3.zero;
            
            if (IsNearWorldCollider(position) || IsBuildingBlocked(position)) 
                return Vector3.zero;
            
            if (IsInRockPrefab(position)) 
                return Vector3.zero;
            
            if (IsNearMonument(position)) 
                return Vector3.zero;

            return hit.point;
        }

        #endregion

        #region Monument

        private void LoadMonuments()
        {
            foreach (var monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                // only load monuments that wouldn't be ideal for encounter (used as blacklist)
                if (monument.IsSafeZone || monument.name.Contains("cave") || monument.name.Contains("power_sub"))
                {
                   _monuments.Add(new Monument(monument));
                }
            }
                
        }

        private class Monument
        {
            public Vector3 Position;
            public Vector3 Size;

            public Monument(MonumentInfo monumentInfo)
            {
                var size = monumentInfo.Bounds.extents;
                if (size.z < 50f) size.z = 120f;

                Size = size;
                Position = monumentInfo.transform.position;
            }
        }

        #endregion

        private class CrateEvent
        {
            private readonly HashSet<ScientistNPC> _npcs = new HashSet<ScientistNPC>();
            private readonly HashSet<BradleyAPC> _apcs = new HashSet<BradleyAPC>();
            private MapMarkerGenericRadius _marker;
            private HackableLockedCrate _crate;
            private Coroutine _coroutine;
            private CargoPlane _plane;
            private Vector3 _position;
            private Timer _timer;
            private GuardedCrateMod _plugin;
            private EventSetting _settings;

            public EventSetting Settings => _settings;

            public CrateEvent(GuardedCrateMod plugin)
            {
                _plugin = plugin;
            }

            #region State Management

            public void SetupEvent(EventSetting settings)
            {
                _settings = settings;
                _position = _plugin.GetPosition();

                if (_position == Vector3.zero) return;

                SpawnPlane();

                _plugin?.AddEvent(this);
            }

            public void SetupEventOnPlayer(EventSetting settings, IPlayer player)
            {
                _settings = settings;
                _position = _plugin.GetPlayerPosition(player);

                if (_position == Vector3.zero) return;

                SpawnPlane();

                _plugin?.AddEvent(this);
            }

            public void StartEvent()
            {
                SpawnMarker();
                SpawnCrate();
                ResetTimer();
                
                if (_config.EnableNotifications)
                    _plugin.MessageAll("EventStarted", _settings.EventName, GetGrid(_position), GetTime((int)_settings.EventDuration));
            }

            public void StopEvent(bool completed = false)
            {
                _timer?.Destroy();

                StopRoutine();
                DespawnPlane();
                DespawnCrate(completed);
                DespawnAI();

                _plugin?.DelEvent(this);
            }

            public void RefreshEvent()
            {
                if (!IsValid(_marker)) return;

                _marker.SendUpdate();
            }

            #endregion

            #region Coroutine

            public void StartRoutine()
            {
                _coroutine = CommunityEntity.ServerInstance.StartCoroutine(SpawnAI());
            }

            private void StopRoutine()
            {
                if (_coroutine != null)
                    CommunityEntity.ServerInstance.StopCoroutine(_coroutine);

                _coroutine = null;
            }

            #endregion

            #region Timer

            private void ResetTimer()
            {
                _timer?.Destroy();
                _timer = _plugin.timer.Once(_settings.EventDuration, () => StopEvent());
            }

            #endregion

            #region Cache

            void CacheAdd(ScientistNPC player)
            {
                _npcs.Add(player);
                _plugin.AddEntity(player, this);
            }

            void CacheRemove(ScientistNPC player)
            {
                _npcs.Remove(player);
                _plugin.DelEntity(player);
            }

            void CacheAdd(BradleyAPC player)
            {
                _apcs.Add(player);
                _plugin.AddEntity(player, this);
            }

            void CacheRemove(BradleyAPC player)
            {
                _apcs.Remove(player);
                _plugin.DelEntity(player);
            }

            #endregion

            #region Plane

            private void SpawnPlane()
            {
                _plane = (CargoPlane)GameManager.server.CreateEntity(PLANE_PREFAB);
                _plane.InitDropPosition(_position);
                _plane.Spawn();

                var cargoComponent = _plane.GetOrAddComponent<CargoComponent>();
                cargoComponent.CrateEvent = this;
                cargoComponent.SetPlaneSpeed();
            }

            #endregion

            #region Marker

            private void SpawnMarker()
            {
                _marker = (MapMarkerGenericRadius)GameManager.server.CreateEntity(MARKER_PREFAB, _position);
                _marker.enableSaving = false;
                _marker.color1 = GetColor(_settings.MarkerColor);
                _marker.color2 = GetColor(_settings.MarkerBorderColor);
                _marker.alpha = _settings.MarkerOpacity;
                _marker.radius = 0.5f;
                _marker.Spawn();
            }

            #endregion

            #region Crate

            private void SpawnCrate()
            {
                _crate = (HackableLockedCrate)GameManager.server.CreateEntity(CRATE_PREFAB, _position + Vector3.up * 100f, Quaternion.identity);
                _crate.enableSaving = false;
                _crate.shouldDecay = false;
                _crate.Spawn();
                _crate.gameObject.AddComponent<CrateComponent>();

                _marker.SetParent(_crate);
                _marker.transform.localPosition = Vector3.zero;
                _marker.SendUpdate();

                RefillCrate();

                _plugin.AddEntity(_crate, this);
            }

            #endregion

            #region AI

            private IEnumerator SpawnAI()
            {
                // TODO: spawn apc ONLY if setting is hard or elite
                var apc_position = FindPointOnNavmesh(_position, 7f);

                if (apc_position is Vector3)
                    SpawnApc((Vector3)apc_position, Quaternion.LookRotation((Vector3)apc_position - _position));
                else
                    Debug.Log("Failed to find position for AI");

                for (var i = 0; i < _settings.NpcCount; i++)
                {
                    var position = FindPointOnNavmesh(_position, 5f);

                    if (position is Vector3)
                        SpawnNpc((Vector3)position, Quaternion.LookRotation((Vector3)position - _position));
                    else
                        Debug.Log("Failed to find position for AI");

                    yield return CoroutineEx.waitForEndOfFrame;
                }
            }

            private void SpawnApc(Vector3 position, Quaternion rotation) 
            {
                var apc = (BradleyAPC)GameManager.server.CreateEntity(BRADLEY_PREFAB, position, rotation);
                apc.enableSaving = false;
                // npc.displayName = _settings.NpcName;
                // npc.startHealth = _settings.NpcHealth;
                apc.Spawn();
                
                apc.targetList.Clear();
                apc.ClearPath();

                CacheAdd(apc);
            }

            private void SpawnNpc(Vector3 position, Quaternion rotation)
            {
                // random NPC prefab for group diversity
                var prefab = NPC_PREFABS[Random.Range(0, NPC_PREFABS.Length - 1)];

                var npc = (ScientistNPC)GameManager.server.CreateEntity(prefab, position, rotation);
                npc.enableSaving = false;
                npc.displayName = _settings.NpcName;
                npc.startHealth = _settings.NpcHealth;
                npc.Spawn();
                npc.InitializeHealth(_settings.NpcHealth, _settings.NpcHealth);

                CacheAdd(npc);

                GiveKit(npc, _settings);

                _plugin.NextFrame(() =>
                {
                    if (npc == null || npc.IsDestroyed) return;

                    npc.Brain.Navigator.Agent.agentTypeID = -1372625422;
                    npc.Brain.Navigator.DefaultArea = "Walkable";
                    npc.Brain.Navigator.Init(npc, npc.Brain.Navigator.Agent);
                    npc.Brain.ForceSetAge(0);

                    npc.Brain.states.Remove(AIState.TakeCover);
                    npc.Brain.states.Remove(AIState.Roam);
                    npc.Brain.states.Remove(AIState.Chase);
                    npc.Brain.states.Remove(AIState.Idle);
                    npc.Brain.states.Remove(AIState.Flee);

                    npc.Brain.AddState(new TakeCoverState { brain = npc.Brain, Position = position });
                    npc.Brain.AddState(new RoamState(npc) { brain = npc.Brain, Position = position });
                    npc.Brain.AddState(new IdleState(npc) { brain = npc.Brain });
                    npc.Brain.AddState(new ChaseState(npc) { brain = npc.Brain });

                    npc.Brain.Navigator.BestCoverPointMaxDistance = _settings.NpcRoamRadius / 2;
                    npc.Brain.Navigator.BestRoamPointMaxDistance = _settings.NpcRoamRadius;
                    npc.Brain.Navigator.MaxRoamDistanceFromHome = _settings.NpcRoamRadius;
                    npc.Brain.Navigator.PlaceOnNavMesh();
                    npc.Brain.Senses.Init(npc, npc.Brain, 5f, _settings.NpcLookRadius, _settings.NpcLookRadius + 5f, -1f, true, true, true, _settings.NpcLookRadius, false, false, true, EntityType.Player, false);
                });
            }

            #endregion

            #region Loot

            private List<LootItem> GenerateLoot()
            {
                var num = 100;

                var lootItems = new List<LootItem>();

                do
                {
                    var lootItem = _settings.CustomLoot.GetRandom();
                    if (lootItems.Contains(lootItem)) continue;

                    lootItems.Add(lootItem);
                } while (lootItems.Count < _settings.MaxLootItems && --num > 0);

                return lootItems;
            }

            private void RefillCrate()
            {
                if (!_settings.UseLoot || _settings.CustomLoot.Count <= 0) return;

                var lootItems = GenerateLoot();
                
                _crate.inventory.Clear();
                _crate.inventory.capacity = lootItems.Count;
                ItemManager.DoRemoves();

                foreach (var lootItem in lootItems)
                    lootItem.CreateItem()?.MoveToContainer(_crate.inventory);

                lootItems.Clear();
            }

            private void DetermineWinner(BasePlayer player) {
                if (_npcs.Any(x => x != null && !x.IsDestroyed))
                {
                    ResetTimer();
                    return;
                }

                if (_apcs.Any(x => x != null && !x.IsDestroyed))
                {
                    ResetTimer();
                    return;
                }

                if (player != null)
                {
                    var winner = string.Empty;

                    if (_settings.LockToPlayer && _plugin?.HackableLock != null)
                        _plugin.HackableLock?.Call("LockCrateToPlayer", player, _crate);

                    if (_settings.DisplayClan && _plugin?.Clans != null)
                        winner = _plugin.Clans?.Call<string>("GetClanOf", player.userID);

                    if (string.IsNullOrEmpty(winner))
                        winner = player.displayName;

                    if (_config.EnableNotifications)
                        _plugin?.MessageAll("EventEnded", GetGrid(_position), winner);

                    Interface.CallHook("OnGuardedCrateEventEnded", player, _crate);
                    StopEvent(true);
                }
                else
                {
                    if (_config.EnableNotifications)
                        _plugin.MessageAll("EventClear", GetGrid(_position));

                    StopEvent();
                }
            }

            #endregion

            #region Cleanup

            private void DespawnCrate(bool completed = false)
            {
                if (!IsValid(_crate)) return;

                if (!completed)
                {
                    _crate.Kill();
                    return;
                }

                if (_settings.AutoHack)
                {
                    _crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - _settings.AutoHackSeconds;
                    _crate.StartHacking();
                }

                _crate.shouldDecay = true;
                _crate.RefreshDecay();
            }

            private void DespawnPlane()
            {
                if (!IsValid(_plane)) return;

                _plane.Kill();
            }

            private void DespawnAI()
            {
                foreach (var apc in _apcs.ToList())
                {
                    if (!IsValid(apc)) continue;

                    apc.Kill();
                }

                _apcs.Clear();

                foreach (var npc in _npcs.ToList())
                {
                    if (!IsValid(npc)) continue;

                    npc.Kill();
                }

                _npcs.Clear();
            }

            #endregion

            #region Oxide Hooks

            private void OnEntityDeath(BradleyAPC apc, HitInfo info)
            {
                CacheRemove(apc);
                BasePlayer player = info.Initiator as BasePlayer;
                DetermineWinner(player);
            }

            public void OnNPCDeath(ScientistNPC npc, BasePlayer player)
            {
                CacheRemove(npc);
                DetermineWinner(player);
            }

            public object OnCanHackCrate()
            {
                if (_npcs.Count > 0) return false;

                return null;
            }

            // don't attack fellow NPCs buddys
            public object CanBradleyApcTarget(BradleyAPC apc, ScientistNPC npc)
            {
                // todo: don't interfere with other bradleys on  map (is apc, ours?)
                //return false;
                return npc != null && _npcs.Any(x => x.userID == npc.userID) ? (object)false : null;
            }

            #endregion
        }

        #endregion

        #region AI States

        public class RoamState : ScientistBrain.BasicAIState
        { 
            private readonly float _maxRoamRadius = 10f;
            private readonly ScientistNPC _npc;
            private StateStatus status = StateStatus.Error;
            public Vector3 Position;

            public RoamState(ScientistNPC npc) : base(AIState.Roam) { _npc = npc; }

            public override float GetWeight() => 0.0f;
            
            public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateEnter(brain, entity);
                status = StateStatus.Error;
                
                if (brain.Navigator.SetDestination(Position, BaseNavigator.NavigationSpeed.Slow))
                    status = StateStatus.Running;
                
                _npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, true);
            }

            public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateLeave(brain, entity);
                Stop();
                _npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            }

            public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
            {
                base.StateThink(delta, brain, entity);
                
                if (Vector3.Distance(entity.transform.position, Position) >= _maxRoamRadius) 
                    brain.Navigator.SetDestination(Position, BaseNavigator.NavigationSpeed.Fast);
                else if (!brain.Navigator.Moving) 
                    brain.Navigator.SetDestination(GetRoamPosition(entity), BaseNavigator.NavigationSpeed.Slowest);
                
                return StateStatus.Running;
            }

            private void Stop() => brain.Navigator.Stop();
            
            private Vector3 GetRoamPosition(BaseEntity entity)
            {
                var positionAround = brain.PathFinder.GetRandomPositionAround(Position, 0f, _maxRoamRadius - 2f < 0f ? 0f : _maxRoamRadius - 2f);
                
                NavMeshHit meshHit;
                
                if (NavMesh.SamplePosition(positionAround, out meshHit, 2f, _npc.NavAgent.areaMask))
                {
                    NavMeshPath path = new NavMeshPath();
                    
                    if (NavMesh.CalculatePath(entity.transform.position, meshHit.position, _npc.NavAgent.areaMask, path)) 
                        positionAround = path.status == NavMeshPathStatus.PathComplete ? meshHit.position : path.corners.Last();
                    else 
                        positionAround = Position;
                }
                else 
                    positionAround = Position;
                
                return positionAround;
            }
        }

        public class IdleState : ScientistBrain.BasicAIState
        {
            private readonly ScientistNPC _npc;

            public IdleState(ScientistNPC npc) : base(AIState.Idle) { _npc = npc; }

            public override float GetWeight() => 50f;

            public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateEnter(brain, entity);
                _npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, true);
            }

            public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateLeave(brain, entity);
                _npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, false);
            }
        }

        public class ChaseState : ScientistBrain.BasicAIState
        {
            private StateStatus status = StateStatus.Error;
            private float nextPositionUpdateTime;

            public ChaseState(ScientistNPC npc) : base(AIState.Chase)
            {
                AgrresiveState = true;
            }

            public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateEnter(brain, entity);
                status = StateStatus.Error;
                if (brain.PathFinder == null) 
                    return;

                status = StateStatus.Running;
                nextPositionUpdateTime = 0.0f;
            }

            public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateLeave(brain, entity);
                Stop();
            }

            public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
            {
                if (status == StateStatus.Error)
                    return status;

                var baseEntity = brain.Events.Memory.Entity.Get(brain.Events.CurrentInputMemorySlot);
                if (baseEntity == null) 
                    return StateStatus.Error;
                
                var distance = Vector3.Distance(entity.transform.position, baseEntity.transform.position);

                if (brain.Senses.Memory.IsLOS(baseEntity) || (double)distance <= brain.Navigator.MaxRoamDistanceFromHome)
                    brain.Navigator.SetFacingDirectionEntity(baseEntity);
                else
                    brain.Navigator.ClearFacingDirectionOverride();

                brain.Navigator.SetCurrentSpeed(distance <= brain.Navigator.MaxRoamDistanceFromHome ? 
                    BaseNavigator.NavigationSpeed.Normal : 
                    BaseNavigator.NavigationSpeed.Fast);
                
                if (Time.time > nextPositionUpdateTime)
                {
                    nextPositionUpdateTime = Time.time + Random.Range(0.5f, 1f);
                    brain.Navigator.SetDestination(baseEntity.transform.position, BaseNavigator.NavigationSpeed.Normal);
                }

                return brain.Navigator.Moving ? 
                    StateStatus.Running : 
                    StateStatus.Finished;
            }

            private void Stop()
            {
                brain.Navigator.Stop();
                brain.Navigator.ClearFacingDirectionOverride();
            }
        }

        public class TakeCoverState : ScientistBrain.BasicAIState
        {
            private StateStatus status = StateStatus.Error;
            private BaseEntity coverFromEntity;
            public Vector3 Position;

            public TakeCoverState() : base(AIState.TakeCover) {  }

            public override void StateEnter(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateEnter(brain, entity);
                status = StateStatus.Running;
                if (StartMovingToCover())
                    return;
                
                status = StateStatus.Error;
            }

            public override void StateLeave(BaseAIBrain brain, BaseEntity entity)
            {
                base.StateLeave(brain, entity);
                brain.Navigator.ClearFacingDirectionOverride();
                ClearCoverPointUsage(entity);
            }

            public override StateStatus StateThink(float delta, BaseAIBrain brain, BaseEntity entity)
            {
                FaceCoverFromEntity();

                if (status == StateStatus.Error)
                    return status;
                
                return brain.Navigator.Moving ? 
                    StateStatus.Running : 
                    StateStatus.Finished;
            }
            
            private void ClearCoverPointUsage(BaseEntity entity)
            {
                var aiPoint = brain.Events.Memory.AIPoint.Get(4);
                if (aiPoint == null)
                    return;
                
                aiPoint.ClearIfUsedBy(entity);
            }

            private bool StartMovingToCover() => brain.Navigator.SetDestination(Position, BaseNavigator.NavigationSpeed.Normal);

            private void FaceCoverFromEntity()
            {
                coverFromEntity = brain.Events.Memory.Entity.Get(brain.Events.CurrentInputMemorySlot);
                if (coverFromEntity != null)
                    brain.Navigator.SetFacingDirectionEntity(coverFromEntity);
            }
        }

        #endregion

        #region NavMesh

        private static NavMeshHit navmeshHit;

        private static RaycastHit raycastHit;

        private static Collider[] _buffer = new Collider[256];

        private const int WORLD_LAYER = 65536;

        private static object FindPointOnNavmesh(Vector3 targetPosition, float maxDistance = 4f)
        {
            for (var i = 0; i < 20; i++)
            {
                var position = targetPosition + Random.onUnitSphere * maxDistance;
                
                position.y = GetSpawnHeight(position);
                
                if (!NavMesh.SamplePosition(position, out navmeshHit, 10f, 1)) 
                    continue;
                
                if (IsInRockPrefab(navmeshHit.position)) 
                    continue;
                
                if (IsNearWorldCollider(navmeshHit.position)) 
                    continue;

                return navmeshHit.position;
            }

            return null;
        }

        private static float GetSpawnHeight(Vector3 target)
        {
            var y = TerrainMeta.HeightMap.GetHeight(target);
            var p = TerrainMeta.HighestPoint.y + 250f;

            if (Physics.Raycast(new Vector3(target.x, p, target.z), Vector3.down, out raycastHit, target.y + p, Layers.Mask.World, QueryTriggerInteraction.Ignore))
                y = Mathf.Max(y, raycastHit.point.y);

            return y;
        }

        private static bool IsInRockPrefab(Vector3 position)
        {
            Physics.queriesHitBackfaces = true;
            var isInRock = Physics.Raycast(position, Vector3.up, out raycastHit, 20f, WORLD_LAYER, QueryTriggerInteraction.Ignore) && BlockedColliders.Any(s => raycastHit.collider?.gameObject?.name.Contains(s, System.Globalization.CompareOptions.OrdinalIgnoreCase) ?? false);
            Physics.queriesHitBackfaces = false;
            return isInRock;
        }

        private static bool IsNearWorldCollider(Vector3 position)
        {
            Physics.queriesHitBackfaces = true;
            var count = Physics.OverlapSphereNonAlloc(position, 2f, _buffer, WORLD_LAYER, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = false;

            var removed = 0;

            for (var i = 0; i < count; i++)
            {
                if (AcceptedColliders.Any(s => _buffer[i].gameObject.name.Contains(s)))
                    removed++;
            }

            return removed != count;
        }

        private static bool IsBuildingBlocked(Vector3 position) => GamePhysics.CheckSphere<BuildingPrivlidge>(position, _config.SpawnDistanceFromBases);

        private bool IsNearMonument(Vector3 position)
        {
            foreach (var monument in _monuments)
            {
                if (Vector3Ex.Distance2D(position, monument.Position) <= 100f)
                    return true;
            }

            return false;
        }

        private static readonly string[] AcceptedColliders = { "road", "rocket_factory", "train_track", "runway", "_grounds", "concrete_slabs", "office", "industrial", "junkyard" };

        private static readonly string[] BlockedColliders = { "cliff", "rock", "junk", "range", "invisible" };

        #endregion

        #region Command Methods

        private void StartEvent(IPlayer player, string[] args)
        {
            if (args.Length == 0)
            {
                player.Message(Lang("InvalidSyntax", player.Id));
                return;
            }

            StartEvent(string.Join(" ", args));

            player.Message(Lang("CreateEvent", player.Id));
        }

        private void StartEventOnPlayer(IPlayer player, string[] args)
        {
            if (args.Length == 0)
            {
                player.Message(Lang("InvalidSyntax", player.Id));
                return;
            }

            StartEventOnPlayer(player, string.Join(" ", args));

            player.Message(Lang("CreateEvent", player.Id));
        }

        private void StopEvents(IPlayer player)
        {
            StopEvents();

            player.Message(Lang("CleanEvents", player.Id));
        }

        #endregion

        #region Command

        private void GCCommand(IPlayer player, string command, string[] args)
        {
            if (args.Length < 1)
            {
                player.Message(Lang("InvalidSyntax", player.Id));
                return;
            }

            switch (args[0].ToLower())
            {
                case "start":
                    StartEvent(player, args.Skip(1).ToArray());
                    break;
                case "stop":
                    StopEvents(player);
                    break;
                case "me":
                    StartEventOnPlayer(player, args.Skip(1).ToArray());
                    break;
                default:
                    player.Message(Lang("InvalidSyntax", player.Id));
                    break;
            }
        }

        #endregion

        #region Component

        private class CargoComponent : MonoBehaviour
        {
            public CrateEvent CrateEvent;
            private CargoPlane _plane;
            private bool _hasDropped;

            private void Awake()
            {
                _plane = GetComponent<CargoPlane>();
                _plane.dropped = true;
            }

            private void Update()
            {
                var time = Mathf.InverseLerp(0.0f, _plane.secondsToTake, _plane.secondsTaken);

                if (_hasDropped || !((double)time >= 0.5)) return;

                _hasDropped = true;

                CrateEvent?.StartEvent();

                Destroy(this);
            }

            public void SetPlaneSpeed()
            {
                _plane.secondsTaken = 0;
                _plane.secondsToTake = CrateEvent.Settings.CargoSpeed;
            }
        }

        private class CrateComponent : MonoBehaviour
        {
            private BaseEntity _chute;
            private BaseEntity _crate;
            private bool _hasLanded;

            private void Awake()
            {
                _crate = gameObject.GetComponent<BaseEntity>();
                _crate.GetComponent<Rigidbody>().drag = 0.9f;

                SpawnChute();
            }

            private void FixedUpdate()
            {
                if (_hasLanded)
                    return;

                var size = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.5f, 1f, Vis.colBuffer, 1218511105);
                if (size <= 0) 
                    return;

                _hasLanded = true;

                RemoveChute();

                Interface.CallHook("OnCrateLanded", _crate);
            }

            private void SpawnChute()
            {
                _chute = GameManager.server.CreateEntity(CHUTE_PREFAB, transform.position, Quaternion.identity);
                _chute.enableSaving = false;
                _chute.Spawn();
                _chute.SetParent(_crate);
                _chute.transform.localPosition = Vector3.zero;
                _chute.SendNetworkUpdate();
            }

            private void RemoveChute()
            {
                if (!IsValid(_chute))
                    return;

                _chute.Kill();
                _chute = null;
            }
        }

        #endregion

        #region Helpers

        private void MessageAll(string key, params object[] args) => server.Broadcast(Lang(key, null, args), Lang("Prefix"));

        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        private static string GetGrid(Vector3 position) => PhoneController.PositionToGridCoord(position);

        private static Color GetColor(string hex)
        {
            Color color;
            return ColorUtility.TryParseHtmlString(hex, out color) ? color : Color.yellow;
        }

        private static string GetTime(int seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{time.Hours:D2}h:{time.Minutes:D2}m:{time.Seconds:D2}s";
        }

        private static void GiveKit(ScientistNPC npc, EventSetting settings)
        {
            if (npc == null || settings == null) 
                return;
            
            if (settings.UseKits)
            {
                npc.inventory.Strip();
                
                Interface.Oxide.CallHook("GiveKit", npc, settings.Kits.GetRandom());
            }

            for (var i = 0; i < npc.inventory.containerBelt.itemList.Count; i++)
            {
                var item = npc.inventory.containerBelt.itemList[i];
                if (item == null) continue;

                var projectile = (item?.GetHeldEntity() as HeldEntity) as BaseProjectile;
                if (projectile == null) return;

                if (_config.EffectiveWeaponRange.ContainsKey(item.info.shortname))
                    projectile.effectiveRange = _config.EffectiveWeaponRange[item.info.shortname];
                else
                    projectile.effectiveRange = settings.NpcAttackRadius;

                projectile.CanUseAtMediumRange = true;
                projectile.CanUseAtLongRange = true;
            }

            npc.Invoke(() => npc.EquipWeapon(), 0.25f);
        }

        private static bool IsValid(BaseEntity entity) => entity != null && !entity.IsDestroyed;

        #endregion
    }
}