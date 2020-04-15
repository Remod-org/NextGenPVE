#define DEBUG
using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using System.Text;
using Oxide.Core.Plugins;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("Pure PVE", "RFC1920", "1.0.0")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class PurePVE : RustPlugin
    {
        #region vars
        Dictionary<string, PurePVERuleSet> pverulesets = new Dictionary<string, PurePVERuleSet>();
        Dictionary<string, PurePVERule> pverules = new Dictionary<string, PurePVERule>();

        private ConfigData configData;

        [PluginReference]
        Plugin ZoneManager;
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));
        #endregion

        #region init
        void Init()
        {
            LoadConfigVariables();
        }

        void OnServerInitialized()
        {
            LoadData();
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["helptext1"] = "Can I play, daddy?"
            }, this);
        }

        void Unload()
        {
        }

        void LoadData()
        {
            pverules = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVERule>>(this.Name + "_rules");
            if(pverules.Count == 0)
            {
                LoadDefaultRules();
            }

            pverulesets = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVERuleSet>>(this.Name + "_rulesets");
            if(pverulesets.Count == 0)
            {
                LoadDefaultRuleset();
            }
            SaveData();
        }

        void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name + "_rules", pverules);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "_rulesets", pverulesets);
        }
        #endregion

        #region Main
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if(entity == null || hitInfo == null) return null;
            if(hitInfo.damageTypes.Has(Rust.DamageType.Decay)) return null;

            bool canhurt = EvaluateRulesets(hitInfo.Initiator, entity as BaseEntity);

            if(canhurt)
            {
#if DEBUG
                Puts("Damage allowed!");
#endif
                return null;
            }
            // Damage NOT allowed...
            return true;
        }

        private bool EvaluateRulesets(BaseEntity source, BaseEntity target)
        {
            string stype = source.GetType().Name;
            string ttype = target.GetType().Name;
            string zone  = null;

            if(configData.Options.UseZoneManager)
            {
                string[] sourcezone = GetEntityZones(source);
                string[] targetzone = GetEntityZones(target);

                if(sourcezone.Length > 0 && targetzone.Length > 0)
                {
                    foreach(string z in sourcezone)
                    {
                        if(targetzone.Contains(z))
                        {
                            zone = z;
                            break;
                        }
                    }
                }
            }
            else if(configData.Options.UseLiteZones)
            {
            }

            // zone
            foreach(KeyValuePair<string,PurePVERuleSet> pveruleset in pverulesets)
            {
#if DEBUG
                Puts($"Checking for match in ruleset {pveruleset.Key} for {stype} attacking {ttype}, default: {pveruleset.Value.damage.ToString()}");
#endif

                bool rulematch = false;

                foreach(string rule in pveruleset.Value.except)
                {
#if DEBUG
                    string exclusions = string.Join(",", pveruleset.Value.exclude);
                    Puts($"  Evaluating rule {rule} with exclusions: {exclusions}");
#endif
                    rulematch = EvaluateRule(rule, stype, ttype, pveruleset.Value.exclude);
                    if(rulematch)
                    {
#if DEBUG
                        Puts($"  Matched rule {pveruleset.Key}");
#endif
                        return true;
                    }
                    //else
                    //{
                    //    return false;
                    //}
                }
            }
#if DEBUG
            Puts($"NO RULESET MATCH!");
#endif
            return pverulesets["default"].damage;
        }

        private bool EvaluateRule(string rulename, string stype, string ttype, List<string> exclude)
        {
            bool srcmatch = false;
            bool tgtmatch = false;

            foreach(string src in pverules[rulename].source)
            {
#if DEBUG
                Puts($"    Checking for source match, {stype} == {src}?");
#endif
                if(stype == src)
                {
#if DEBUG
                    Puts($"       Matched {stype} to {src}");
#endif
                    if(exclude.Contains(stype))
                    {
#if DEBUG
                        Puts($"         Exclusion match!");
                        break;
#endif
                    }
                    srcmatch = true;
                    break;
                }
            }
            foreach(string tgt in pverules[rulename].target)
            {
#if DEBUG
                Puts($"    Checking for target match, {ttype} == {tgt}?");
#endif
                if(ttype == tgt)
                {
#if DEBUG
                    Puts($"       Matched {ttype} to {tgt}");
#endif
                    if(exclude.Contains(ttype))
                    {
#if DEBUG
                        Puts($"         Exclusion match!");
                        break;
#endif
                    }
                    tgtmatch = true;
                    break;
                }
            }

            if(srcmatch && tgtmatch) return true;

            return false;
        }

//        object OnTrapTrigger(BaseTrap trap, GameObject go) maybe...

//        object OnPlayerAttack(BasePlayer attacker, HitInfo hitinfo) ????????

//        object CanBeTargeted(BasePlayer target, MonoBehaviour turret) OH YEAH!!!!
//            configData.Options.NPCAutoTurretTargetsPlayers
//            configData.Options.NPCAutoTurretTargetsNPCs

        private string[] GetEntityZones(BaseEntity entity)
        {
            if(entity is BasePlayer)
            {
                 return (string[]) ZoneManager.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
            }
            else if(entity.IsValid())
            {
                 return (string[]) ZoneManager.Call("GetEntityZoneIDs", new object[] { entity });
            }
            return null;
        }
        #endregion

        #region config
        private void LoadConfigVariables()
        {
            configData = Config.ReadObject<ConfigData>();
            SaveConfig(configData);
        }
        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            var config = new ConfigData();
            SaveConfig(config);
        }
        void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }
        class ConfigData
        {
            public Options Options = new Options();
        }
        class Options
        {
            public bool UseZoneManager = true;
            public bool UseLiteZones = false;
            public bool NPCAutoTurretTargetsPlayers = true;
            public bool NPCAutoTurretTargetsNPCs = true;
        }
        #endregion

        #region classes
        public class PurePVERuleSet
        {
            public bool damage;
            public List<string> except;
            public List<string> exclude;
            public string zone;
            public string schedule;
        }

        public class PurePVERule
        {
            public string description;
            public bool damage;
            public List<string> source;
            public List<string> target;
        }
        #endregion

        #region load_defaults
        void LoadDefaultRuleset()
        {
            pverulesets.Add("default", new PurePVERuleSet()
            {
                damage = false, zone = null, schedule = null,
                except = new List<string>() { "animal_player", "player_animal", "animal_animal", "player_minicopter", "player_npc", "npc_player", "player_resources", "npcturret_player", "npcturret_animal", "npcturret_npc" },
                exclude = new List<string>() {}
            });
        }

        // Default rules
        void LoadDefaultRules()
        {
            pverules.Add("npc_player", new PurePVERule()
            {
                description = "NPCs can damage players", damage = true,
                source = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("player_npc", new PurePVERule()
            {
                description = "Players can damage NPCs", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer" }
            });
            pverules.Add("player_player", new PurePVERule()
            {
                description = "Players can damage players", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("player_resources", new PurePVERule()
            {
                description = "Players can damage resources", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "ResourceEntity" }//, "TreeEntity", "OreResourceEntity" }
            });
            pverules.Add("players_traps", new PurePVERule()
            {
                description = "Traps can damage players", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "TeslaCoil", "BearTrap", "FlameTurret", "Landmine", "GunTrap", "ReactiveTarget", "spikes.floor" }
            });
            pverules.Add("traps_players", new PurePVERule()
            {
                description = "Traps can damage players", damage = true,
                source = new List<string>() { "TeslaCoil", "BearTrap", "FlameTurret", "Landmine", "GunTrap", "ReactiveTarget", "spikes.floor" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("player_animal", new PurePVERule()
            {
                description = "Players can damage Animals", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" }
            });
            pverules.Add("animal_player", new PurePVERule()
            {
                description = "Animals can damage players ", damage = true,
                source = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("animal_animal", new PurePVERule()
            {
                description = "Animals can damage players ", damage = true,
                source = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" },
                target = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" }
            });
            pverules.Add("helicopter_player", new PurePVERule()
            {
                description = "Helicopter can damage players", damage = true,
                source = new List<string>() { "BaseHeli" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("player_minicopter", new PurePVERule()
            {
                description = "Players can damage Minicopter", damage = true,
                source = new List<string>() { "BasePlayer" },
                target = new List<string>() { "Minicopter" }
            });
            pverules.Add("minicopter_player", new PurePVERule()
            {
                description = "Minicopter can damage players", damage = true,
                source = new List<string>() { "Minicopter" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("highwalls_player", new PurePVERule()
            {
                description = "Highwalls can damage players", damage = true,
                source = new List<string>() { "wall.external.high.stone", "wall.external.high.wood", "gates.external.high.wood", "gates.external.high.stone" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("npcturret_player", new PurePVERule()
            {
                description = "NPCAutoTurret can damage player", damage = true,
                source = new List<string>() { "NPCAutoTurret" },
                target = new List<string>() { "BasePlayer" }
            });
            pverules.Add("npcturret_animal", new PurePVERule()
            {
                description = "NPCAutoTurret can damage player", damage = true,
                source = new List<string>() { "NPCAutoTurret" },
                target = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf" }
            });
            pverules.Add("npcturret_npc", new PurePVERule()
            {
                description = "NPCAutoTurret can damage npcs", damage = true,
                source = new List<string>() { "NPCAutoTurret" },
                target = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer" }
            });
        }
        #endregion
    }
}
