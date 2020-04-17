//#define DEBUG
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
    [Info("Pure PVE", "RFC1920", "1.0.2")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class PurePVE : RustPlugin
    {
        #region vars
        Dictionary<string, PurePVEEntities> pveentities = new Dictionary<string, PurePVEEntities>();
        Dictionary<string, PurePVERule> pverules = new Dictionary<string, PurePVERule>();
        Dictionary<string, PurePVERule> custom = new Dictionary<string, PurePVERule>();
        Dictionary<string, PurePVERuleSet> pverulesets = new Dictionary<string, PurePVERuleSet>();

        private const string permPurePVEUse = "purepve.use";
        private const string permPurePVEAdmin = "purepve.admin";
        private ConfigData configData;

        [PluginReference]
        Plugin ZoneManager, LiteZones, HumanNPC;

        private string logfilename = "log";
        private bool dolog = false;
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));
        #endregion

        #region init
        void Init()
        {
            LoadConfigVariables();
            AddCovalenceCommand("pvelog", "cmdPurePVElog");
            permission.RegisterPermission(permPurePVEUse, this);
            permission.RegisterPermission(permPurePVEAdmin, this);
        }

        void OnServerInitialized()
        {
            LoadData();
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["notauthorized"] = "You don't have permission to use this command.",
                ["logging"] = "Logging set to {0}"
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
            custom.Clear();

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

        #region Oxide_hooks
        object OnTrapTrigger(BaseTrap trap, GameObject go)
        {
            var player = go.GetComponent<BasePlayer>();
            if(trap == null || player == null) return null;

            var cantraptrigger = Interface.CallHook("CanEntityTrapTrigger", new object[] { trap, player });
            if(cantraptrigger != null && cantraptrigger is bool && (bool)cantraptrigger) return null;

            if(configData.Options.TrapsIgnorePlayers) return null;

            string stype;
            string ttype;
            if(EvaluateRulesets(trap, player, out stype, out ttype)) return true;

            return null;
        }

        private object CanBeTargeted(BasePlayer target, MonoBehaviour turret)
        {
            if(target == null || turret == null) return null;
//            Puts($"Checking whether {turret.name} can target {target.displayName}");

            object canbetargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { target, turret as BaseEntity });
            if(canbetargeted != null && canbetargeted is bool && (bool)canbetargeted) return null;

            var npcturret = turret as NPCAutoTurret;

            // Check global config options
            if(npcturret != null && ((HumanNPC && IsHumanNPC(target)) || target.IsNpc))
            {
                if(!configData.Options.NPCAutoTurretTargetsNPCs) return false;
            }
            else if(npcturret != null && !configData.Options.NPCAutoTurretTargetsPlayers)
            {
                return false;
            }
            else if(npcturret == null && ((HumanNPC && IsHumanNPC(target)) || target.IsNpc))
            {
                if(!configData.Options.AutoTurretTargetsNPCs) return false;
            }
            else if(npcturret == null && !configData.Options.AutoTurretTargetsPlayers)
            {
                return false;
            }

            // Check rulesets
            string stype;
            string ttype;
            if(npcturret != null)
            {
                if(!EvaluateRulesets(npcturret as BaseEntity, target, out stype, out ttype)) return false;
            }
            else
            {
                if(!EvaluateRulesets(turret as BaseEntity, target, out stype, out ttype)) return false;
            }

            // CanBeTargeted == yes
            return null;
        }

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if(entity == null || hitInfo == null) return null;
            if(hitInfo.damageTypes.Has(Rust.DamageType.Decay)) return null;

            AttackEntity realturret;
            if(IsAutoTurret(hitInfo, out realturret))
            {
                hitInfo.Initiator = realturret as BaseEntity;
            }

//            Puts($"attacker: {hitInfo.Initiator.ShortPrefabName}, victim: {entity.ShortPrefabName}"); return true;
            string stype;
            string ttype;
            bool canhurt = EvaluateRulesets(hitInfo.Initiator, entity as BaseEntity, out stype, out ttype);

            if(stype == "BasePlayer")
            {
                if((hitInfo.Initiator as BasePlayer).IPlayer.HasPermission(permPurePVEAdmin))
                {
                    Puts("Admin god!");
                    return null;
                }
            }

            if(canhurt)
            {
                DoLog($"DAMAGE ALLOWED for {stype} attacking {ttype}");
                return null;
            }
            else
            {
                DoLog($"dAMAGE BLOCKED for {stype} attacking {ttype}");
            }
            return true;
        }
        #endregion

        #region Main
        private bool EvaluateRulesets(BaseEntity source, BaseEntity target, out string stype, out string ttype)
        {
            stype = source.GetType().Name;
            ttype = target.GetType().Name;
            string zone  = null;

            // Special case since HumanNPC contains a BasePlayer object
            if(stype == "BasePlayer" && HumanNPC && IsHumanNPC(source)) stype = "HumanNPC";
            if(ttype == "BasePlayer" && HumanNPC && IsHumanNPC(target)) ttype = "HumanNPC";


            //var turret = source.Weapon?.GetComponentInParent<AutoTurret>();

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
                DoLog($"Checking for match in ruleset {pveruleset.Key} for {stype} attacking {ttype}, default: {pveruleset.Value.damage.ToString()}");

                bool rulematch = false;

                foreach(string rule in pveruleset.Value.except)
                {
//                    string exclusions = string.Join(",", pveruleset.Value.exclude);
//                    Puts($"  Evaluating rule {rule} with exclusions: {exclusions}");
                    rulematch = EvaluateRule(rule, stype, ttype, pveruleset.Value.exclude);
                    if(rulematch)
                    {
                        DoLog($"Matched rule {pveruleset.Key}", 1);
                        return true;
                    }
                    //else
                    //{
                    //    return false;
                    //}
                }
            }
            DoLog($"NO RULESET MATCH!");
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
                DoLog($"Checking for source match, {stype} == {src}?", 2);
                if(stype == src)
                {
                    DoLog($"Matched {stype} to {src} in {rulename} and target {ttype}", 3);
//                    if(exclude.Contains(stype))
//                    {
//                        srcmatch = false; // ???
//                        DoLog($"Exclusion match for source {stype}", 4);
//                        break;
//                    }
                    smatch = stype;
                    srcmatch = true;
                    break;
                }
            }
            foreach(string tgt in pverules[rulename].target)
            {
                DoLog($"Checking for target match, {ttype} == {tgt}?", 2);
                if(ttype == tgt)
                {
                    DoLog($"       Matched {ttype} to {tgt} in {rulename} and source {stype}", 3);
                    if(exclude.Contains(ttype))
                    {
                        tgtmatch = false; // ???
                        DoLog($"Exclusion match for target {ttype}", 4);
                        break;
                    }
                    tmatch = ttype;
                    tgtmatch = true;
                    break;
                }
            }

            if(srcmatch && tgtmatch)
            {
                DoLog($"Matching rule {rulename} for {smatch} attacking {tmatch}");
                return true;
            }

            return false;
        }

        private void DoLog(string message, int indent = 0)
        {
            if(dolog) LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
        }

        [Command("pvelog")]
        void cmdPurePVElog(IPlayer player, string command, string[] args)
        {
            if(!player.HasPermission(permPurePVEUse)) { Message(player, "notauthorized"); return; }

            dolog = !dolog;
            Message(player, "logging", dolog.ToString());
        }
        #endregion

        #region Specialized_checks
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

        //private bool IsHumanNPC(BaseEntity player) => (bool)HumanNPC?.Call("IsHumanNPC", player as BasePlayer);
        private bool IsHumanNPC(BaseEntity player)
        {
            if(HumanNPC)
            {
                return (bool)HumanNPC?.Call("IsHumanNPC", player as BasePlayer);
            }
            else
            {
                var pl = player as BasePlayer;
                return pl.userID < 76560000000000000L && pl.userID > 0L && !pl.IsDestroyed;
            }
        }

        private bool IsBaseHeli(HitInfo hitInfo)
        {
            if(hitInfo.Initiator is BaseHelicopter
               || (hitInfo.Initiator != null && (hitInfo.Initiator.ShortPrefabName.Equals("oilfireballsmall") || hitInfo.Initiator.ShortPrefabName.Equals("napalm"))))
            {
                return true;
            }

            else if(hitInfo.WeaponPrefab != null)
            {
                if(hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli") || hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli_napalm"))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsAutoTurret(HitInfo hitInfo, out AttackEntity weapon)
        {
            // Check for turret initiator
            var turret = hitInfo.Weapon?.GetComponentInParent<AutoTurret>();
            if(turret != null)
            {
                weapon = hitInfo.Weapon;
                return true;
            }

            weapon = null;
            return false;
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
            public bool AutoTurretTargetsPlayers = false;
            public bool AutoTurretTargetsNPCs = false;
            public bool TrapsIgnorePlayers = false;
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
