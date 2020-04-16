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
    [Info("Pure PVE", "RFC1920", "1.0.1")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class PurePVE : RustPlugin
    {
        #region vars
        Dictionary<string, PurePVERuleSet> pverulesets = new Dictionary<string, PurePVERuleSet>();
        Dictionary<string, PurePVERule> pverules = new Dictionary<string, PurePVERule>();
        Dictionary<string, PurePVEEntities> pveentities = new Dictionary<string, PurePVEEntities>();
        Dictionary<string, PurePVERule> custom = new Dictionary<string, PurePVERule>();

        private const string permPurePVEAdmin = "purepve.admin";
        private ConfigData configData;

        [PluginReference]
        Plugin ZoneManager, LiteZones;
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

        void LoadData()
        {
            pveentities = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVEEntities>>(this.Name + "/" + this.Name + "_entities");
            if(pveentities.Count == 0)
            {
                LoadDefaultEntities();
            }
            pverules = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVERule>>(this.Name + "/" + this.Name + "_rules");
            if(pverules.Count == 0)
            {
                LoadDefaultRules();
            }

            // Merge and overwrite from this file if it exists.
            custom = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVERule>>(this.Name + "/" + this.Name + "_custom");
            if(custom.Count > 0)
            {
                foreach(KeyValuePair<string, PurePVERule> rule in custom)
                {
                    pverules[rule.Key] = rule.Value;
                }
            }
            custom.Clear();// = new Dictionary<string, PurePVERule>();

            pverulesets = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, PurePVERuleSet>>(this.Name + "/" + this.Name + "_rulesets");
            if(pverulesets.Count == 0)
            {
                LoadDefaultRuleset();
            }
            SaveData();
        }

        void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/" + Name + "_entities", pveentities);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/" + Name  + "_rules", pverules);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/" + Name  + "_rulesets", pverulesets);
        }
        #endregion

        #region Main
        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if(entity == null || hitInfo == null) return null;
            if(hitInfo.damageTypes.Has(Rust.DamageType.Decay)) return null;

            if(hitInfo.Initiator is BasePlayer)
            {
                if((hitInfo.Initiator as BasePlayer).IPlayer.HasPermission(permPurePVEAdmin))
                {
#if DEBUG
                    Puts("Admin god!");
#endif
                    return null;
                }
            }

            bool canhurt = EvaluateRulesets(hitInfo.Initiator, entity as BaseEntity);

            if(canhurt)
            {
#if DEBUG
                Puts($"Damage allowed for {hitInfo.Initiator.GetType().Name} and {entity.GetType().Name}");
#endif
                return null;
            }
            else
            {
#if DEBUG
                Puts($"Damage blocked for {hitInfo.Initiator.GetType().Name} and {entity.GetType().Name}");
#endif
            }
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
                List<string> sz = (List<string>) LiteZones?.Call("GetEntityZones", new object[] { source });
                List<string> tz = (List<string>) LiteZones?.Call("GetEntityZones", new object[] { target });
                if(sz != null && sz.Count > 0 && tz != null && tz.Count > 0)
                {
                    foreach(string z in sz)
                    {
                        if(tz.Contains(z))
                        {
                            zone = z;
                            break;
                        }
                    }
                }
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
//#if DEBUG
//                    string exclusions = string.Join(",", pveruleset.Value.exclude);
//                    Puts($"  Evaluating rule {rule} with exclusions: {exclusions}");
//#endif
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
//#if DEBUG
//            Puts($"NO RULESET MATCH!");
//#endif
            return false;//pverulesets["default"].damage;
        }

        // Compare rulename to source and target looking for match unless also excluded
        private bool EvaluateRule(string rulename, string stype, string ttype, List<string> exclude)
        {
            bool srcmatch = false;
            bool tgtmatch = false;
            string smatch = null;
            string tmatch = null;

            foreach(string src in pverules[rulename].source)
            {
//#if DEBUG
//                Puts($"    Checking for source match, {stype} == {src}?");
//#endif
                if(stype == src)
                {
//#if DEBUG
//                    Puts($"       Matched {stype} to {src} in {rulename} and target {ttype}");
//#endif
//                    if(exclude.Contains(stype))
//                    {
//                        srcmatch = false; // ???
//#if DEBUG
//                        Puts($"         Exclusion match for source {stype}");
//#endif
//                        break;
//                    }
                    smatch = stype;
                    srcmatch = true;
                    break;
                }
            }
            foreach(string tgt in pverules[rulename].target)
            {
//#if DEBUG
//                Puts($"    Checking for target match, {ttype} == {tgt}?");
//#endif
                if(ttype == tgt)
                {
//#if DEBUG
//                    Puts($"       Matched {ttype} to {tgt} in {rulename} and source {stype}");
//#endif
                    if(exclude.Contains(ttype))
                    {
                        tgtmatch = false; // ???
#if DEBUG
                        Puts($"         Exclusion match for target {ttype}");
#endif
                        break;
                    }
                    tmatch = ttype;
                    tgtmatch = true;
                    break;
                }
            }

            if(srcmatch && tgtmatch)
            {
#if DEBUG
                Puts($"Matching rule {rulename} for {smatch} attacking {tmatch}");
#endif
                return true;
            }

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

        public class PurePVEEntities
        {
            public List<string> types;
        }
        #endregion

        #region load_defaults
        private void LoadDefaultRuleset()
        {
            pverulesets.Add("default", new PurePVERuleSet()
            {
                damage = false, zone = null, schedule = null,
                except = new List<string>() { "animal_player", "player_animal", "animal_animal", "player_minicopter", "player_npc", "npc_player", "player_resources", "npcturret_player", "npcturret_animal", "npcturret_npc" },
                exclude = new List<string>() {}
            });
        }

        // Default entity categories and types to consider
        private void LoadDefaultEntities()
        {
            pveentities.Add("npc", new PurePVEEntities() { types = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer", "Murderer", "Scientist" } });
            pveentities.Add("player", new PurePVEEntities() { types = new List<string>() { "BasePlayer" } });
            pveentities.Add("resource", new PurePVEEntities() { types = new List<string>() { "ResourceEntity", "LootContainer" } });
            pveentities.Add("trap", new PurePVEEntities() { types = new List<string>() { "TeslaCoil", "BearTrap", "FlameTurret", "Landmine", "GunTrap", "ReactiveTarget", "spikes.floor" } });
            pveentities.Add("animal", new PurePVEEntities() { types = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" } });
            pveentities.Add("helicopter", new PurePVEEntities() { types = new List<string>() { "BaseHeli" } });
            pveentities.Add("minicopter", new PurePVEEntities() { types = new List<string>() { "Minicopter" } });
            pveentities.Add("highwall", new PurePVEEntities() { types = new List<string>() { "wall.external.high.stone", "wall.external.high.wood", "gates.external.high.wood", "gates.external.high.stone" } });
            pveentities.Add("npcturret", new PurePVEEntities() { types = new List<string>() { "NPCAutoTurret" } });
        }

        // Default rules which can be applied
        private void LoadDefaultRules()
        {
            pverules.Add("npc_player", new PurePVERule() { description = "npc can damage player", damage = true, source = pveentities["npc"].types, target = pveentities["player"].types });
            pverules.Add("player_npc", new PurePVERule() { description = "Player can damage npc", damage = true, source = pveentities["player"].types, target = pveentities["npc"].types });
            pverules.Add("player_player", new PurePVERule() { description = "Player can damage player", damage = true, source = pveentities["player"].types, target = pveentities["player"].types });
            pverules.Add("player_resources", new PurePVERule() { description = "Player can damage resource", damage = true, source = pveentities["player"].types, target = pveentities["resource"].types });
            pverules.Add("players_traps", new PurePVERule() { description = "Player can damage trap", damage = true, source = pveentities["player"].types, target = pveentities["trap"].types });
            pverules.Add("traps_players", new PurePVERule() { description = "Trap can damage player", damage = true, source = pveentities["trap"].types, target = pveentities["player"].types });
            pverules.Add("player_animal", new PurePVERule() { description = "Player can damage animal", damage = true, source = pveentities["player"].types, target = pveentities["animal"].types });
            pverules.Add("animal_player", new PurePVERule() { description = "Animal can damage player", damage = true, source = pveentities["animal"].types, target = pveentities["player"].types });
            pverules.Add("animal_animal", new PurePVERule() { description = "Animal can damage animal", damage = true, source = pveentities["animal"].types, target = pveentities["animal"].types });
            pverules.Add("helicopter_player", new PurePVERule() { description = "Helicopter can damage player", damage = true, source = pveentities["helicopter"].types, target = pveentities["player"].types });
            pverules.Add("player_minicopter", new PurePVERule() { description = "Player can damage minicopter", damage = true, source = pveentities["player"].types, target = pveentities["minicopter"].types });
            pverules.Add("minicopter_player", new PurePVERule() { description = "Minicopter can damage player", damage = true, source = pveentities["minicopter"].types, target = pveentities["player"].types });
            pverules.Add("highwalls_player", new PurePVERule() { description = "Highwall can damage player", damage = true, source = pveentities["highwall"].types, target = pveentities["player"].types });
            pverules.Add("npcturret_player", new PurePVERule() { description = "NPCAutoTurret can damage player", damage = true, source = pveentities["npcturret"].types, target = pveentities["player"].types });
            pverules.Add("npcturret_animal", new PurePVERule() { description = "NPCAutoTurret can damage animal", damage = true, source = pveentities["npcturret"].types, target = pveentities["animal"].types });
            pverules.Add("npcturret_npc", new PurePVERule() { description = "NPCAutoTurret can damage npc", damage = true, source = pveentities["npcturret"].types, target = pveentities["npc"].types });
        }
        #endregion
    }
}
