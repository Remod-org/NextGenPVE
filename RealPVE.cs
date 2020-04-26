using Oxide.Core;
//using Oxide.Core.Database;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using System.Data.SQLite;
using System.IO;

// TODO
// Add the actual schedule handling...
// Finish work on custom rule editor gui (src/target)
// Sanity checking for overlapping rule/zone combinations.  Schedule may have impact.
// Add setting global flags

namespace Oxide.Plugins
{
    [Info("Real PVE", "RFC1920", "1.0.13")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class RealPVE : RustPlugin
    {
        #region vars
        Dictionary<string, RealPVEEntities> rpveentities = new Dictionary<string, RealPVEEntities>();
        Dictionary<string, RealPVEEntities> custom_entities = new Dictionary<string, RealPVEEntities>();
        Dictionary<string, RealPVERule> rpverules = new Dictionary<string, RealPVERule>();
        Dictionary<string, RealPVERule> custom_rules = new Dictionary<string, RealPVERule>();
        Dictionary<string, RealPVERuleSet> rpverulesets = new Dictionary<string, RealPVERuleSet>();
        // Ruleset to multiple zones
        Dictionary<string, RealPVEZoneMap> rpvezonemaps = new Dictionary<string, RealPVEZoneMap>();

        private const string permRealPVEUse = "realpve.use";
        private const string permRealPVEAdmin = "realpve.admin";
        private const string permRealPVEGod = "realpve.god";
        private ConfigData configData;

        SQLiteConnection sqlConnection;

        [PluginReference]
        Plugin ZoneManager, LiteZones, HumanNPC, Friends, Clans, RustIO;

        private string logfilename = "log";
        private bool dolog = false;
        private bool enabled = true;

        Timer scheduleTimer;

        const string RPVERULELIST = "realpve.rulelist";
        const string RPVEEDITRULESET = "realpve.ruleseteditor";
        const string RPVERULEEDIT = "realpve.ruleeditor";
        const string RPVEVALUEEDIT = "realpve.value";
        const string RPVESCHEDULEEDIT = "realpve.schedule";
        const string RPVERULESELECT = "realpve.selectrule";
        const string RPVERULEEXCLUSIONS = "realpve.exclusions";
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));
        #endregion

        #region init
        private void Init()
        {
            LoadConfigVariables();
            if (configData.Options.useSQLITE)
            {
                Puts("Creating connection...");
                string cs = $"Data Source={Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}realpve.db;";
                sqlConnection = new SQLiteConnection(cs);
                Puts("Opening...");
                sqlConnection.Open();
            }
            LoadData();
            AddCovalenceCommand("pveenable", "cmdRealPVEenable");
            AddCovalenceCommand("pvelog", "cmdRealPVElog");
            AddCovalenceCommand("pverule", "cmdRealPVEGUI");
            permission.RegisterPermission(permRealPVEUse, this);
            permission.RegisterPermission(permRealPVEAdmin, this);
            permission.RegisterPermission(permRealPVEGod, this);
            enabled = true;
        }

        private void OnServerInitialized()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["notauthorized"] = "You don't have permission to use this command.",
                ["realpverulesets"] = "RealPVE Rulesets",
                ["realpveruleset"] = "RealPVE Ruleset",
                ["realpverule"] = "RealPVE Rule",
                ["realpveruleselect"] = "RealPVE Rule Select",
                ["realpvevalue"] = "RealPVE Ruleset Value",
                ["realpveexclusions"] = "RealPVE Ruleset Exclusions",
                ["realpveschedule"] = "RealPVE Ruleset Schedule",
                ["scheduling"] = "Schedules should be in the format of 'day(s);starttime;endtime'.  Use * for all days.  Enter 0 to clear.",
                ["current2chedule"] = "Currently scheduled for day: {0} from {1} until {2}",
                ["noschedule"] = "Not currently scheduled",
                ["schedulenotworking"] = "Scheduling is not yet working...",
                ["defload"] = "RESET",
                ["default"] = "default",
                ["none"] = "none",
                ["close"] = "Close",
                ["clone"] = "Clone",
                ["allow"] = "Allow",
                ["block"] = "Block",
                ["save"] = "Save",
                ["edit"] = "Edit",
                ["clicktoedit"] = "^Click to Edit^",
                ["editname"] = "Edit Name",
                ["standard"] = "Standard",
                ["automated"] = "Automated",
                ["add"] = "Add",
                ["all"] = "All",
                ["true"] = "True",
                ["false"] = "False",
                ["editing"] = "Editing",
                ["source"] = "Source",
                ["target"] = "Target",
                ["exclude"] = "Exclude from",
                ["enableset"] = "Enable set to {0}",
                ["enabled"] = "Enabled",
                ["disabled"] = "Disabled",
                ["genabled"] = "Globally Enabled",
                ["gdisabled"] = "Globally Disabled",
                ["zone"] = "Zone",
                ["schedule"] = "Schedule",
                ["editing"] = "Editing",
                ["select"] = "Select",
                ["damage"] = "Damage",
                ["stock"] = "Stock",
                ["custom"] = "Custom",
                ["lookup"] = "lookup",
                ["defaultdamage"] = "Default Damage",
                ["damageexceptions"] = "Damage Exceptions",
                ["rule"] = "Rule",
                ["rules"] = "Rules",
                ["logging"] = "Logging set to {0}"
            }, this);
        }

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, RPVERULELIST);
                CuiHelper.DestroyUi(player, RPVERULEEDIT);
                CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);
                CuiHelper.DestroyUi(player, RPVEEDITRULESET);
                CuiHelper.DestroyUi(player, RPVERULESELECT);
                CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);
            }

            if (scheduleTimer != null) scheduleTimer.Destroy();
            if (configData.Options.useSQLITE) sqlConnection.Close();
        }

        private void LoadData()
        {
            if (configData.Options.useSQLITE)
            {
                SQLiteCommand r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_entities'", sqlConnection);
                SQLiteDataReader rentry = r.ExecuteReader();

                while(rentry.Read())
                {
                    //if (rentry.GetInt32(0) == 0)
                    if(rentry.GetValue(0) == null)
                    {
                        LoadDefaultEntities(true);
                    }
                };

                r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_rules'", sqlConnection);
                rentry = r.ExecuteReader();
                while(rentry.Read())
                {
                    if(rentry.GetValue(0) == null)
                    {
                        LoadDefaultRules(true);
                    }
                };

                r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_rulesets'", sqlConnection);
                rentry = r.ExecuteReader();
                while(rentry.Read())
                {
                    if(rentry.GetValue(0) == null)
                    {
                        LoadDefaultRuleset(true);
                    }
                }
            }
            else
            {
                rpveentities = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVEEntities>>(this.Name + "/rpve_entities");
                if (rpveentities.Count == 0)
                {
                    LoadDefaultEntities();
                }

                // Merge and overwrite from this file if it exists.
                custom_entities = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVEEntities>>(this.Name + "/rpve_customentities");
                if (custom_entities.Count > 0)
                {
                    foreach (KeyValuePair<string, RealPVEEntities> ent in custom_entities)
                    {
                        rpveentities[ent.Key] = ent.Value;
                        rpveentities[ent.Key].custom = true;
                    }
                }
                custom_entities.Clear();

                rpverules = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERule>>(this.Name + "/rpve_rules");
                if (rpverules.Count == 0)
                {
                    LoadDefaultRules();
                }

                // Merge and overwrite from this file if it exists.
                custom_rules = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERule>>(this.Name + "/rpve_customrules");
                if (custom_rules.Count > 0)
                {
                    foreach (KeyValuePair<string, RealPVERule> rule in custom_rules)
                    {
                        rpverules[rule.Key] = rule.Value;
                    }
                }
                custom_rules.Clear();

                rpverulesets = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERuleSet>>(this.Name + "/rpve_rulesets");
                if (rpverulesets.Count == 0)
                {
                    LoadDefaultRuleset();
                }

                rpvezonemaps = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVEZoneMap>>(this.Name + "/rpve_zonemaps");
            }

            SortData();
            SaveData();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/rpve_entities", rpveentities);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/rpve_rules", rpverules);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/rpve_rulesets", rpverulesets);
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/rpve_zonemaps", rpvezonemaps);
        }

        private void SortData()
        {
            // Verify no duplicate rules, exclusions, and entities - also sort
            foreach (KeyValuePair<string, RealPVERuleSet> rs in rpverulesets)
            {
                rpverulesets[rs.Key].except = rs.Value.except.Distinct().OrderBy(q => q).ToList();
                if (rpverulesets[rs.Key].src_exclude != null) rpverulesets[rs.Key].src_exclude = rs.Value.src_exclude.Distinct().OrderBy(q => q).ToList();
                if (rpverulesets[rs.Key].tgt_exclude != null) rpverulesets[rs.Key].tgt_exclude = rs.Value.tgt_exclude.Distinct().OrderBy(q => q).ToList();
            }
            foreach (KeyValuePair<string, RealPVEEntities> ent in rpveentities)
            {
                rpveentities[ent.Key].types = ent.Value.types.Distinct().OrderBy(q => q).ToList();
            }
        }
        #endregion

        #region Oxide_hooks
        object OnTrapTrigger(BaseTrap trap, UnityEngine.GameObject go)
        {
            if (!enabled) return null;
            var player = go.GetComponent<BasePlayer>();
            if (trap == null || player == null) return null;

            var cantraptrigger = Interface.CallHook("CanEntityTrapTrigger", new object[] { trap, player });
            if (cantraptrigger != null && cantraptrigger is bool && (bool)cantraptrigger) return null;

            if (configData.Options.TrapsIgnorePlayers) return null;

            string stype;
            string ttype;
            if (EvaluateRulesets(trap, player, out stype, out ttype)) return true;

            return null;
        }

        private object CanBeTargeted(BasePlayer target, UnityEngine.MonoBehaviour turret)
        {
            if (target == null || turret == null) return null;
            if (!enabled) return null;
            //            Puts($"Checking whether {turret.name} can target {target.displayName}");

            object canbetargeted = Interface.CallHook("CanEntityBeTargeted", new object[] { target, turret as BaseEntity });
            if (canbetargeted != null && canbetargeted is bool && (bool)canbetargeted) return null;

            var npcturret = turret as NPCAutoTurret;

            // Check global config options
            if (npcturret != null && ((HumanNPC && IsHumanNPC(target)) || target.IsNpc))
            {
                if (!configData.Options.NPCAutoTurretTargetsNPCs) return false;
            }
            else if (npcturret != null && !configData.Options.NPCAutoTurretTargetsPlayers)
            {
                return false;
            }
            else if (npcturret == null && ((HumanNPC && IsHumanNPC(target)) || target.IsNpc))
            {
                if (!configData.Options.AutoTurretTargetsNPCs) return false;
            }
            else if (npcturret == null && !configData.Options.AutoTurretTargetsPlayers)
            {
                return false;
            }

            // Check rulesets - FIXME SERVER CRASH
            //            string stype;
            //            string ttype;
            //            if(npcturret != null)
            //            {
            //                if(!EvaluateRulesets(npcturret as BaseEntity, target, out stype, out ttype)) return false;
            //            }
            //            else
            //            {
            //                if(!EvaluateRulesets(turret as BaseEntity, target, out stype, out ttype)) return false;
            //            }

            // CanBeTargeted == yes
            return null;
        }

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (entity == null || hitInfo == null) return null;
            if (!enabled) return null;
            if (hitInfo.damageTypes.Has(Rust.DamageType.Decay)) return null;

            AttackEntity realturret;
            if (IsAutoTurret(hitInfo, out realturret))
            {
                hitInfo.Initiator = realturret as BaseEntity;
            }

            //            Puts($"attacker: {hitInfo.Initiator.ShortPrefabName}, victim: {entity.ShortPrefabName}"); return true;
            string stype;
            string ttype;
            bool canhurt = EvaluateRulesets(hitInfo.Initiator, entity as BaseEntity, out stype, out ttype);

            if (stype == "BasePlayer")
            {
                if ((hitInfo.Initiator as BasePlayer).IPlayer.HasPermission(permRealPVEGod))
                {
                    Puts("Admin god!");
                    return null;
                }
            }

            if (canhurt)
            {
                DoLog($"DAMAGE ALLOWED for {stype} attacking {ttype}");
                return null;
            }
            else
            {
                DoLog($"DAMAGE BLOCKED for {stype} attacking {ttype}");
            }
            return true;
        }
        #endregion

        #region inbound_hooks
        private bool TogglePVE(bool on = true) => enabled = on;

        private bool TogglePVERuleset(string rulesetname, bool on = true) => rpverulesets[rulesetname].enabled = on;

        private bool AddOrUpdateMapping(string key, string rulesetname)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (rulesetname == null) return false;

            if (configData.Options.useSQLITE)
            {
                return true;
            }

            DoLog($"AddOrUpdateMapping called for ruleset: {rulesetname}, zone: {key}", 0);

            if (rpverulesets.ContainsKey(rulesetname))
            {
                if (rpvezonemaps.ContainsKey(rulesetname) && !rpvezonemaps[rulesetname].map.Contains(key))
                {
                    rpvezonemaps[rulesetname].map.Add(key);
                }
                else
                {
                    rpvezonemaps.Add(rulesetname, new RealPVEZoneMap() { map = new List<string>() { key } });
                }
                rpverulesets[rulesetname].zone = "lookup";
                SaveData();
                return true;
            }
            else
            {
                rpverulesets.Add(rulesetname, new RealPVERuleSet() { damage = true, enabled = true, automated = true, zone = "lookup", except = new List<string>(), src_exclude = new List<string>(), tgt_exclude = new List<string>() });
                if (rpvezonemaps.ContainsKey(rulesetname)) rpvezonemaps.Remove(rulesetname);
                rpvezonemaps.Add(rulesetname, new RealPVEZoneMap() { map = new List<string>() { key } });
                SaveData();
                return true;
            }
        }

        private bool RemoveMapping(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            List<string> foundrs = new List<string>();

            DoLog($"RemoveMapping called for zone: {key}", 0);

            if (configData.Options.useSQLITE)
            {
                return true;
            }
            foreach (KeyValuePair<string, RealPVERuleSet> rpveruleset in rpverulesets)
            {
                if (rpveruleset.Value.zone == "lookup")
                {
                    foundrs.Add(rpveruleset.Key);
                    rpvezonemaps.Remove(rpveruleset.Key);
                }
            }
            if (foundrs.Count > 0)
            {
                foreach (var found in foundrs)
                {
                    rpverulesets[found].zone = null;
                }
                SaveData();
            }

            return true;
        }
        #endregion

        #region Main
        private bool EvaluateRulesets(BaseEntity source, BaseEntity target, out string stype, out string ttype)
        {
            if (source == null)
            {
                stype = null;
                ttype = null;
                return false;
            }
            if (target == null)
            {
                stype = null;
                ttype = null;
                return false;
            }
            stype = source.GetType().Name;
            ttype = target.GetType().Name;
            string zone = "default";
            bool hasBP = false;

            // Special case since HumanNPC contains a BasePlayer object
            if (stype == "BasePlayer" && HumanNPC && IsHumanNPC(source)) stype = "HumanNPC";
            if (ttype == "BasePlayer" && HumanNPC && IsHumanNPC(target)) ttype = "HumanNPC";

            //var turret = source.Weapon?.GetComponentInParent<AutoTurret>();

            if (stype == "BasePlayer" && ttype == "BuildingBlock")
            {
                if (PlayerOwnsItem(source as BasePlayer, target)) hasBP = true;
            }

            bool zmatch = false;
            if (configData.Options.useZoneManager)
            {
                string[] sourcezone = GetEntityZones(source);
                string[] targetzone = GetEntityZones(target);

                if (sourcezone.Length > 0 && targetzone.Length > 0)
                {
                    foreach (string z in sourcezone)
                    {
                        if (targetzone.Contains(z))
                        {
                            string zName = (string)ZoneManager?.Call("GetZoneName", z);
                            if (zName != null) zone = zName;
                            else zone = z;
                            DoLog($"Found zone {zone}", 1);
                            break;
                        }
                    }
                }
            }
            else if (configData.Options.useLiteZones)
            {
                List<string> sz = (List<string>)LiteZones?.Call("GetEntityZones", new object[] { source });
                List<string> tz = (List<string>)LiteZones?.Call("GetEntityZones", new object[] { target });
                if (sz != null && sz.Count > 0 && tz != null && tz.Count > 0)
                {
                    foreach (string z in sz)
                    {
                        if (tz.Contains(z))
                        {
                            zone = z;
                            break;
                        }
                    }
                }
            }

            if (configData.Options.useSQLITE)
            {
                bool foundmatch = false;
                bool damage = true;
                bool enabled = false;
                string rulesetname = null;
                string source_type = stype;
                string target_type = ttype;

                string src = null; string tgt = null;
                SQLiteCommand selectSrc = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{source_type}'", sqlConnection);
                SQLiteDataReader ss = selectSrc.ExecuteReader();
                while(ss.Read())
                {
                    src = ss.GetString(0);
                    break;
                }

                SQLiteCommand selectTgt = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{target_type}'", sqlConnection);
                SQLiteDataReader st = selectTgt.ExecuteReader();
                while(st.Read())
                {
                    tgt = st.GetString(0);
                    break;
                }

                SQLiteCommand r = new SQLiteCommand("SELECT DISTINCT name, zone, damage, enabled FROM rpve_rulesets", sqlConnection);
                SQLiteDataReader rentry = r.ExecuteReader();
                while(rentry.Read())
                {
                     rulesetname = rentry.GetString(0);
                     zone = rentry.GetString(1);
                     damage = rentry.GetBoolean(2);
                     enabled = rentry.GetBoolean(3);

                    DoLog($"Checking {rulesetname} for {source_type} attacking {target_type}");
                    Puts($"Checking {rulesetname} for {source_type} attacking {target_type}");
                    if (src != null && tgt != null)
                    {
                        DoLog($"Found {source_type} attacking {target_type}.  Checking ruleset {rulesetname}");
                        Puts($"Found {source_type} attacking {target_type}.  Checking ruleset {rulesetname}");
                        // source and target exist - verify that they are not excluded
                        int en = enabled ? 1 : 0;
                        string rquery = $"SELECT enabled, src_exclude, tgt_exclude FROM rpve_rulesets WHERE name='{rulesetname}' AND enabled='{en}' AND exception='{src}_{tgt}'";
                        //Puts(rquery);
                        SQLiteCommand rq = new SQLiteCommand(rquery, sqlConnection);
                        SQLiteDataReader entry = rq.ExecuteReader();
                   
                        while(entry.Read())
                        {
                            DoLog($"Found match for {source_type} attacking {target_type}");
                            Puts($"Found match for {source_type} attacking {target_type}");
                            string foundsrc = entry.GetValue(1).ToString();
                            string foundtgt = entry.GetValue(2).ToString();
                            if (foundsrc.Contains(source_type))
                            {
                                Puts($"Exclusion for {source_type}");
                                foundmatch = false;
                                break;
                            }
                            else if (foundtgt.Contains(target_type))
                            {
                                Puts($"Exclusion for {target_type}");
                                foundmatch = false;
                                break;
                            }
                            else
                            {
                                Puts($"No exclusions for {source_type} to {target_type}");
                                foundmatch = true;
                                break;
                            }
                       }
                    }
                }
                if (foundmatch)
                {
                    Puts($"No rule match, setting damage to NOT {damage.ToString()}");
                    return !damage;
                }
                else
                {
                    Puts($"Rule match, setting damage to {damage.ToString()}");
                    return damage;
                }
            }

            foreach (KeyValuePair<string, RealPVERuleSet> rpveruleset in rpverulesets)
            {
                if (!rpveruleset.Value.enabled) continue;

                if (rpveruleset.Value.zone == "lookup" && rpvezonemaps.ContainsKey(rpveruleset.Key))
                {
                    if (!rpvezonemaps[rpveruleset.Key].map.Contains(zone))
                    {
                        //                        DoLog($"Skipping check due to zone {zone} mismatch");
                        continue;
                    }
                }
                else if (zone != "default" && zone != rpveruleset.Value.zone)
                {
                    //                    DoLog($"Skipping check due to zone {zone} mismatch");
                    continue;
                }
                zmatch = true;

                DoLog($"Checking for match in ruleset {rpveruleset.Key} (zone {zone}) for {stype} attacking {ttype}, default: {rpveruleset.Value.damage.ToString()}");

                bool rulematch = false;

                foreach (string rule in rpveruleset.Value.except)
                {
                    //                    string exclusions = string.Join(",", rpveruleset.Value.exclude);
                    //                    Puts($"  Evaluating rule {rule} with exclusions: {exclusions}");
                    rulematch = EvaluateRule(rule, stype, ttype, rpveruleset.Value.src_exclude, rpveruleset.Value.tgt_exclude);
                    if (rulematch)
                    {
                        //                        DoLog($"Matched rule {rpveruleset.Key}", 1); //Log volume FIXME
                        return true;
                    }
                    else if (ttype == "BuildingBlock" && hasBP)
                    {
                        DoLog($"Damage allowed based on HonorBuildingPrivilege for {stype} attacking {ttype}", 1);
                        return true;
                    }
                }
                if (zmatch) return rpveruleset.Value.damage;
            }

            //            DoLog($"NO RULESET MATCH!");
            return false;
        }

        // Compare rulename to source and target looking for match unless also excluded
        private bool EvaluateRule(string rulename, string stype, string ttype, List<string> src_exclude, List<string> tgt_exclude)
        {
            bool srcmatch = false;
            bool tgtmatch = false;
            string smatch = null;
            string tmatch = null;

            foreach (string src in rpverules[rulename].source)
            {
                //                DoLog($"Checking for source match, {stype} == {src}?", 1); //Log volume FIXME
                if (stype == src)
                {
                    DoLog($"Matched {src} in {rulename} with target {ttype}", 2);
                    if (src_exclude.Contains(stype))
                    {
                        srcmatch = false; // ???
                        DoLog($"Exclusion match for source {stype}", 3);
                        break;
                    }
                    smatch = stype;
                    srcmatch = true;
                    break;
                }
            }
            foreach (string tgt in rpverules[rulename].target)
            {
                //                DoLog($"Checking for target match, {ttype} == {tgt}?", 1); //Log volume FIXME
                if (ttype == tgt && srcmatch)
                {
                    DoLog($"Matched {tgt} in {rulename} with source {stype}", 2);
                    if (tgt_exclude.Contains(ttype))
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

            if (srcmatch && tgtmatch)
            {
                if (rulename.Contains("npcturret")) return true; // Log volume FIXME
                DoLog($"Matching rule {rulename} for {smatch} attacking {tmatch}");
                return true;
            }
            //            else if(tgtmatch)
            //            {
            //                DoLog($"No source match", 2);
            //            }
            //            else if(srcmatch)
            //            {
            //                DoLog($"No target match", 2);
            //            }

            return false;
        }

        private void DoLog(string message, int indent = 0)
        {
            if (!enabled) return;
            if (message.Contains("Turret")) return; // Log volume FIXME
            if (dolog) LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
        }

        [Command("pveenable")]
        private void cmdRealPVEenable(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permRealPVEAdmin)) { Message(player, "notauthorized"); return; }

            enabled = !enabled;
            if (args.Length > 0)
            {
                if (args[0] == "gui") GUIRuleSets(player.Object as BasePlayer);
            }
            else
            {
                Message(player, "enableset", enabled.ToString());
            }
        }

        [Command("pvelog")]
        private void cmdRealPVElog(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permRealPVEAdmin)) { Message(player, "notauthorized"); return; }

            dolog = !dolog;
            Message(player, "logging", dolog.ToString());
        }

        [Command("pverule")]
        private void cmdRealPVEGUI(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.HasPermission(permRealPVEAdmin)) { Message(iplayer, "notauthorized"); return; }
            var player = iplayer.Object as BasePlayer;

            if (args.Length > 0)
            {
                string debug = string.Join(",", args); Puts($"{debug}");

                switch (args[0])
                {
                    case "editruleset":
                        //e.g.: pverule editruleset {rulesetname} damage 0
                        //      pverule editruleset {rulesetname} name {newname}
                        //      pverule editruleset {rulesetname} zone {zonename}
                        // This is where we actually make the edit.
                        if (args.Length > 3)
                        {
                            string rs = args[1];
                            string setting = args[2];
                            string newval = args[3];
                            CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                            switch (setting)
                            {
                                case "defload":
                                    rs = "default";
                                    rpverulesets.Remove(rs);
                                    LoadDefaultRuleset();
                                    break;
                                case "enable":
                                    rpverulesets[rs].enabled = GetBoolValue(newval);
                                    break;
                                case "damage":
                                    rpverulesets[rs].damage = GetBoolValue(newval);
                                    break;
                                case "name":
                                    string newrs = args[3];
                                    if (!rpverulesets.ContainsKey(newrs))
                                    {
                                        rpverulesets.Add(newrs, rpverulesets[rs]);
                                        rpverulesets.Remove(rs);
                                    }
                                    rs = newrs;
                                    break;
                                case "zone":
                                    if (args[3] == "delete") rpverulesets[rs].zone = null;
                                    else rpverulesets[rs].zone = newval;
                                    break;
                                case "schedule":
                                    if (newval == "0") rpverulesets[rs].schedule = null;
                                    else rpverulesets[rs].schedule = newval;
                                    break;
                                case "except":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} except {pverule.Key} add
                                    //pverule editruleset noturret except npcturret_animal delete
                                    switch (args[4])
                                    {
                                        case "add":
                                            if (!rpverulesets[rs].except.Contains(newval)) rpverulesets[rs].except.Add(newval);
                                            break;
                                        case "delete":
                                            if (rpverulesets[rs].except.Contains(newval)) rpverulesets[rs].except.Remove(newval);
                                            break;
                                    }
                                    if (rpverulesets[rs].except.Count == 0) rpverulesets[rs].src_exclude.Clear(); // No exclude with exceptions...
                                    if (rpverulesets[rs].except.Count == 0) rpverulesets[rs].tgt_exclude.Clear(); // No exclude with exceptions...
                                    break;
                                case "src_exclude":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} src_exclude Horse add
                                    switch (args[4])
                                    {
                                        case "add":
                                            if (rpverulesets[rs].src_exclude == null)
                                            {
                                                rpverulesets[rs].src_exclude = new List<string>() { newval };
                                            }
                                            else if (!rpverulesets[rs].src_exclude.Contains(newval))
                                            {
                                                rpverulesets[rs].src_exclude.Add(newval);
                                            }
                                            break;
                                        case "delete":
                                            if (rpverulesets[rs].src_exclude != null && rpverulesets[rs].src_exclude.Contains(newval)) rpverulesets[rs].src_exclude.Remove(newval);
                                            break;
                                    }
                                    break;
                                case "tgt_exclude":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} tgt_exclude Horse add
                                    switch (args[4])
                                    {
                                        case "add":
                                            if (rpverulesets[rs].tgt_exclude == null)
                                            {
                                                rpverulesets[rs].tgt_exclude = new List<string>() { newval };
                                            }
                                            else if (!rpverulesets[rs].tgt_exclude.Contains(newval))
                                            {
                                                rpverulesets[rs].tgt_exclude.Add(newval);
                                            }
                                            break;
                                        case "delete":
                                            if (rpverulesets[rs].tgt_exclude != null && rpverulesets[rs].tgt_exclude.Contains(newval)) rpverulesets[rs].tgt_exclude.Remove(newval);
                                            break;
                                    }
                                    break;
                            }
                            SaveData();
                            LoadData();
                            GUIRuleSets(player);
                            GUIRulesetEditor(player, rs);
                        }
                        //pverule editruleset {rulesetname} delete
                        //pverule editruleset {rulesetname} zone
                        // This is where we either delete or load the dialog to edit values.
                        else if (args.Length > 2)
                        {
                            switch (args[2])
                            {
                                case "delete":
                                    rpverulesets.Remove(args[1]);
                                    SaveData();
                                    CuiHelper.DestroyUi(player, RPVEEDITRULESET);
                                    GUIRuleSets(player);
                                    break;
                                case "name":
                                case "zone":
                                    GUIEditValue(player, args[1], args[2]);
                                    break;
                                case "schedule":
                                    GUIEditSchedule(player, args[1]);
                                    break;
                                case "except":
                                    GUISelectRule(player, args[1]);
                                    break;
                                case "src_exclude":
                                case "tgt_exclude":
                                    GUISelectExclusion(player, args[1], args[2]);
                                    break;
                                case "clone":
                                    int id = 0;
                                    string oldname = args[1];
                                    string clone = oldname + "1";
                                    while (rpverulesets.ContainsKey(clone))
                                    {
                                        id++;
                                        if (id > 4) break;
                                        clone = oldname + id.ToString();
                                    }
                                    Puts($"Creating clone {clone}");
                                    rpverulesets.Add(clone, rpverulesets[oldname]);
                                    SaveData();
                                    LoadData();
                                    GUIRuleSets(player);
                                    GUIRulesetEditor(player, clone);
                                    break;
                            }
                        }
                        //pverule editruleset {rulesetname}
                        else if (args.Length > 1)
                        {
                            string newname = args[1];
                            if (newname == "add")
                            {
                                int id = 0;
                                newname = "new1";
                                while (rpverulesets.ContainsKey(newname))
                                {
                                    id++;
                                    if (id > 4) break;
                                    newname = "new" + id.ToString();
                                }
                                rpverulesets.Add(newname, new RealPVERuleSet()
                                {
                                    damage = false,
                                    zone = null,
                                    schedule = null,
                                    except = new List<string>() { },
                                    src_exclude = new List<string>() { },
                                    tgt_exclude = new List<string>() { }
                                });
                                SaveData();
                                GUIRuleSets(player);// FIXME
                            }

                            GUIRulesetEditor(player, newname);
                        }
                        break;
                    case "editrule":
                        string rn = args[1];
                        GUIRuleEditor(player, rn);
                        break;
                    case "close":
                        CuiHelper.DestroyUi(player, RPVERULELIST);
                        CuiHelper.DestroyUi(player, RPVERULEEDIT);
                        CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                        CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);
                        CuiHelper.DestroyUi(player, RPVEEDITRULESET);
                        CuiHelper.DestroyUi(player, RPVERULESELECT);
                        break;
                    case "closeexclusions":
                        CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);
                        break;
                    case "closeruleselect":
                        CuiHelper.DestroyUi(player, RPVERULESELECT);
                        break;
                    case "closeruleset":
                        CuiHelper.DestroyUi(player, RPVEEDITRULESET);
                        break;
                    case "closerule":
                        CuiHelper.DestroyUi(player, RPVERULEEDIT);
                        break;
                    case "closerulevalue":
                        CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                        break;
                    case "closeruleschedule":
                        CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);
                        break;
                    default:
                        GUIRuleSets(player);
                        break;
                }
            }
            else
            {
                GUIRuleSets(player);
            }
        }
        #endregion

        #region config
        private void LoadConfigVariables()
        {
            configData = Config.ReadObject<ConfigData>();
            configData.Version = Version;
            SaveConfig(configData);
        }
        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            var config = new ConfigData();
            config.Version = Version;
            SaveConfig(config);
        }
        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }
        class ConfigData
        {
            public Options Options = new Options();
            public VersionNumber Version;
        }
        class Options
        {
            public bool useZoneManager = true;
            public bool useLiteZones = false;
            public bool useSchedule = false;
            public bool useRealtime = true;
            public bool useFriends = false;
            public bool useClans = false;
            public bool useTeams = false;

            public bool NPCAutoTurretTargetsPlayers = true;
            public bool NPCAutoTurretTargetsNPCs = true;
            public bool AutoTurretTargetsPlayers = false;
            public bool AutoTurretTargetsNPCs = false;
            public bool TrapsIgnorePlayers = false;
            public bool HonorBuildingPrivilege = true;
            public bool HonorRelationships = false;

            public bool useSQLITE = false;
        }
        #endregion

        #region GUI
        private void GUIRuleSets(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, RPVERULELIST);

            CuiElementContainer container = UI.Container(RPVERULELIST, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Label(ref container, RPVERULELIST, UI.Color("#ffffff", 1f), Lang("realpverulesets"), 24, "0.2 0.92", "0.65 1");
            UI.Label(ref container, RPVERULELIST, UI.Color("#d85540", 1f), Lang("standard"), 12, "0.66 0.95", "0.72 0.98");
            UI.Label(ref container, RPVERULELIST, UI.Color("#5540d8", 1f), Lang("automated"), 12, "0.73 0.95", "0.79 0.98");
            UI.Label(ref container, RPVERULELIST, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            if (enabled)
            {
                UI.Button(ref container, RPVERULELIST, UI.Color("#55d840", 1f), Lang("genabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
            }
            else
            {
                UI.Button(ref container, RPVERULELIST, UI.Color("#ff2222", 1f), Lang("gdisabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
            }
            UI.Button(ref container, RPVERULELIST, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule close");

            int col = 0;
            int row = 0;
            float[] pb;
            foreach (KeyValuePair<string, RealPVERuleSet> ruleset in rpverulesets)
            {
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                pb = GetButtonPositionP(row, col);
                string rColor = "#d85540";
                if (ruleset.Value.automated) rColor = "#5540d8";

                UI.Button(ref container, RPVERULELIST, UI.Color(rColor, 1f), ruleset.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {ruleset.Key}");
                row++;
            }
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, RPVERULELIST, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset add");

            CuiHelper.AddUi(player, container);
        }

        private void GUIRulesetEditor(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVEEDITRULESET);
            string rulename = rulesetname;
            if (rpverulesets[rulesetname].automated) rulename += " (" + Lang("automated") + ")";

            CuiElementContainer container = UI.Container(RPVEEDITRULESET, UI.Color("3b3b3b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("realpveruleset") + ": " + rulename, 24, "0.15 0.92", "0.7 1");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedulenotworking"), 12, "0.3 0.05", "0.7 0.08");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            UI.Button(ref container, RPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("clone"), 12, "0.63 0.95", "0.69 0.98", $"pverule editruleset {rulesetname} clone");
            if (rpverulesets[rulesetname].enabled)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("enabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 0");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("disabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 1");
            }

            if (rulesetname == "default")
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("defload"), 12, "0.85 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} defload YES");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("editname"), 12, "0.7 0.95", "0.76 0.98", $"pverule editruleset {rulesetname} name");
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("delete"), 12, "0.86 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} delete");
            }
            UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleset");

            string dicolor = "#333333";
            string encolor = "#ff3333";
            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("defaultdamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row += 2;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("zone"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row += 2;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedule"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row = 0; col++;
            pb = GetButtonPositionP(row, col);
            string de = Lang("block"); if (!rpverulesets[rulesetname].damage) de = Lang("allow");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), de, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            if (rpverulesets[rulesetname].except.Count > 11) col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("source"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("target"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row++;
            col = 0;

            pb = GetButtonPositionP(row, col);
            if (rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if (rpverulesets[rulesetname].damage)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("true"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 0");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(dicolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 1");
            }

            col++;
            bool noExceptions = true;
            foreach (string except in rpverulesets[rulesetname].except)
            {
                if (row > 11)
                {
                    row = 1;
                    col++;
                }
                noExceptions = false;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), except, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
                row++;
            }
            if (noExceptions)
            {
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
            }
            else
            {
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }

            col++; row = 1;
            bool noExclusions = true;
            if (!noExceptions) // Cannot exclude from exceptions that do not exist
            {
                if (rpverulesets[rulesetname].src_exclude == null)
                {
                    pb = GetButtonPositionP(row, col);
                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                }
                else
                {
                    foreach (string exclude in rpverulesets[rulesetname].src_exclude)
                    {
                        noExclusions = false;
                        if (row > 11)
                        {
                            row = 0;
                            col++;
                        }
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                        row++;
                    }
                    if (noExclusions)
                    {
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                    }
                    else
                    {
                        pb = GetButtonPositionP(row, col);
                        UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    }
                }
            }

            col++; row = 1;
            noExclusions = true;
            if (!noExceptions) // Cannot exclude from exceptions that do not exist
            {
                if (rpverulesets[rulesetname].tgt_exclude == null)
                {
                    pb = GetButtonPositionP(row, col);
                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                }
                else
                {
                    foreach (string exclude in rpverulesets[rulesetname].tgt_exclude)
                    {
                        noExclusions = false;
                        if (row > 11)
                        {
                            row = 0;
                            col++;
                        }
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                        row++;
                    }
                    if (noExclusions)
                    {
                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                    }
                    else
                    {
                        pb = GetButtonPositionP(row, col);
                        UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    }
                }
            }

            col = 0; row = 3;
            pb = GetButtonPositionP(row, col);
            if (rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if (rpverulesets[rulesetname].zone == "lookup")
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("lookup"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            else if (rpverulesets[rulesetname].zone != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), rpverulesets[rulesetname].zone, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            if (rulesetname != "default")
            {
                //                row++;
                //                pb = GetButtonPositionP(row, col);
                //                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }

            col = 0; row = 5;
            pb = GetButtonPositionP(row, col);
            if (rpverulesets[rulesetname].schedule != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), rpverulesets[rulesetname].schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
                //                row++;
                //                pb = GetButtonPositionP(row, col);
                //                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
            }

            CuiHelper.AddUi(player, container);
        }

        // Select rule to add to ruleset
        private void GUISelectRule(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVERULESELECT);

            CuiElementContainer container = UI.Container(RPVERULESELECT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Label(ref container, RPVERULESELECT, UI.Color("#ffffff", 1f), Lang("realpveruleselect"), 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVERULESELECT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            UI.Label(ref container, RPVERULESELECT, UI.Color("#5555cc", 1f), Lang("stock"), 12, "0.72 0.95", "0.78 0.98");
            UI.Label(ref container, RPVERULESELECT, UI.Color("#55d840", 1f), Lang("enabled"), 12, "0.79 0.95", "0.85 0.98");
            UI.Label(ref container, RPVERULESELECT, UI.Color("#d85540", 1f), Lang("custom"), 12, "0.86 0.95", "0.92 0.98");
            UI.Button(ref container, RPVERULESELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleselect");

            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col);
            foreach (KeyValuePair<string, RealPVERule> rpverule in rpverules)
            {
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                pb = GetButtonPositionP(row, col);
                if (rpverulesets[rulesetname].except.Contains(rpverule.Key))
                {
                    UI.Button(ref container, RPVERULESELECT, UI.Color("#55d840", 1f), rpverule.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rpverule.Key} delete");
                }
                else
                {
                    string ruleColor = "#5555cc";
                    if (rpverule.Value.custom) ruleColor = "#d85540";
                    UI.Button(ref container, RPVERULESELECT, UI.Color(ruleColor, 1f), rpverule.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rpverule.Key} add");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        private void GUISelectExclusion(BasePlayer player, string rulesetname, string srctgt)
        {
            // Need to run over entities based on a check of what is in the rule...
            CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);
            string t = Lang("source"); if (srctgt == "tgt_exclude") t = Lang("target");

            CuiElementContainer container = UI.Container(RPVERULEEXCLUSIONS, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeexclusions");
            UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Lang("realpveexclusions") + " " + t, 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            int col = 0;
            int row = 0;

            List<string> foundit = new List<string>();
            foreach (var rulename in rpverulesets[rulesetname].except)
            {
                string[] st = rulename.Split('_');
                string src = st[0]; string tgt = st[1];

                float[] pb = GetButtonPositionP(row, col);
                switch (srctgt)
                {
                    case "src_exclude":
                        if (!rpveentities.ContainsKey(src)) continue;
                        foreach (var type in rpveentities[src].types)
                        {
                            if (foundit.Contains(type)) continue;
                            foundit.Add(type);
                            if (row > 10)
                            {
                                row = 0;
                                col++;
                            }
                            pb = GetButtonPositionP(row, col);
                            string eColor = "#d85540";

                            if (rpverulesets[rulesetname].src_exclude == null || !rpverulesets[rulesetname].src_exclude.Contains(type))
                            {
                                UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} add");
                            }
                            else
                            {
                                eColor = "#55d840";
                                UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} delete");
                            }
                        }
                        break;
                    case "tgt_exclude":
                        if (!rpveentities.ContainsKey(tgt)) continue;
                        foreach (var type in rpveentities[tgt].types)
                        {
                            if (foundit.Contains(type)) continue;
                            foundit.Add(type);
                            if (row > 10)
                            {
                                row = 0;
                                col++;
                            }
                            pb = GetButtonPositionP(row, col);
                            string eColor = "#d85540";

                            if (rpverulesets[rulesetname].tgt_exclude == null || !rpverulesets[rulesetname].tgt_exclude.Contains(type))
                            {
                                UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} add");
                            }
                            else
                            {
                                eColor = "#55d840";
                                UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} delete");
                            }
                        }
                        break;
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        private void GUIRuleEditor(BasePlayer player, string rulename)
        {
            CuiHelper.DestroyUi(player, RPVERULEEDIT);

            CuiElementContainer container = UI.Container(RPVERULEEDIT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerule");
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("realpverule") + ": " + rulename, 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), "Name", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), "Description", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), "Damage", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), "Source", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), "Target", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row = 0; col = 1;
            pb = GetButtonPositionP(row, col);
            if (!rpverules[rulename].custom)
            {
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), rpverules[rulename].description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), rpverules[rulename].damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), string.Join(",", rpverules[rulename].source), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), string.Join(",", rpverules[rulename].target), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} name");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), rpverules[rulename].description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} description");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), rpverules[rulename].damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} damage");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), string.Join(",", rpverules[rulename].source), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} source");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), string.Join(",", rpverules[rulename].target), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} target");
            }

            CuiHelper.AddUi(player, container);
        }

        private void GUIEditValue(BasePlayer player, string rulesetname, string key = null)
        {
            CuiHelper.DestroyUi(player, RPVEVALUEEDIT);

            CuiElementContainer container = UI.Container(RPVEVALUEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
            UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerulevalue");
            UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("realpvevalue") + ": " + rulesetname + " " + key, 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            int col = 0;
            int row = 0;
            float[] pb = GetButtonPositionZ(row, col);

            switch (key)
            {
                case "name":
                    pb = GetButtonPositionZ(row, col);
                    UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("editname") + ":", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    col++;
                    pb = GetButtonPositionZ(row, col);
                    UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#535353", 1f), rulesetname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    UI.Input(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} name ");
                    break;
                case "zone":
                    string[] zoneIDs = (string[])ZoneManager?.Call("GetZoneIDs");
                    if (rpverulesets[rulesetname].zone == null && rulesetname != "default")
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }
                    else
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#222222", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }

                    row++;
                    pb = GetButtonPositionZ(row, col);
                    if (rpverulesets[rulesetname].zone == "default")
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#55d840", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone default");
                    }
                    else
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#222222", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone default");
                    }

                    row++;
                    foreach (string zoneID in zoneIDs)
                    {
                        if (row > 10)
                        {
                            row = 0;
                            col++;
                        }
                        string zName = (string)ZoneManager?.Call("GetZoneName", zoneID);
                        string zColor = "#222222";
                        bool labelonly = false;
                        if (zName == rpverulesets[rulesetname].zone) zColor = "#55d840";
                        if (rpverulesets[rulesetname].zone == "lookup" && rpvezonemaps.ContainsKey(rulesetname))
                        {
                            if (rpvezonemaps[rulesetname].map.Contains(zoneID)) zColor = "#55d840";
                            labelonly = true;
                        }
                        if (zoneID.ToString() == rpverulesets[rulesetname].zone) zColor = "#55d840";

                        pb = GetButtonPositionZ(row, col);
                        if (labelonly)
                        {
                            UI.Label(ref container, RPVEVALUEEDIT, UI.Color(zColor, 1f), zName + "(" + zoneID.ToString() + ")", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                        }
                        else
                        {
                            UI.Button(ref container, RPVEVALUEEDIT, UI.Color(zColor, 1f), zName + "(" + zoneID.ToString() + ")", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone {zName}");
                        }
                        row++;
                    }
                    break;
                default:
                    CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                    break;
            }

            CuiHelper.AddUi(player, container);
        }

        private void GUIEditSchedule(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);

            string schedule = rpverulesets[rulesetname].schedule;

            CuiElementContainer container = UI.Container(RPVESCHEDULEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
            UI.Button(ref container, RPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleschedule");
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("realpveschedule") + ": " + rulesetname, 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            string fmtschedule = null;
            if (schedule != null)
            {
                try
                {
                    string[] realschedule = schedule.Split(';');//.ToArray();
                    Puts($"Schedule: {realschedule[0]} {realschedule[1]} {realschedule[2]}");
                    // WTF?
                    int day = 0;
                    string dayName = Lang("all") + "(*)";
                    if (int.TryParse(realschedule[0], out day))
                    {
                        dayName = Enum.GetName(typeof(DayOfWeek), day) + "(" + realschedule[0] + ")";
                    }
                    fmtschedule = Lang("currentschedule", null, dayName, realschedule[1], realschedule[2]);
                }
                catch
                {
                    fmtschedule = Lang("noschedule", null, null, null, null);
                    schedule = "none";
                }
            }
            else
            {
                fmtschedule = Lang("noschedule", null, null, null, null);
                schedule = "none";
            }
            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col, 5f);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("scheduling"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row++;
            pb = GetButtonPositionP(row, col, 3f);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), fmtschedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, 4);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#665353", 1f), schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule ");

            CuiHelper.AddUi(player, container);
        }

        private int RowNumber(int max, int count) => Mathf.FloorToInt(count / max);
        private float[] GetButtonPositionP(int rowNumber, int columnNumber, float colspan = 1f)
        {
            float offsetX = 0.05f + (0.126f * columnNumber);
            float offsetY = (0.87f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + (0.226f * colspan), offsetY + 0.03f };
        }

        private float[] GetButtonPositionZ(int rowNumber, int columnNumber)
        {
            float offsetX = 0.05f + (0.156f * columnNumber);
            float offsetY = (0.80f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + 0.296f, offsetY + 0.03f };
        }
        #endregion

        #region Specialized_checks
        private string[] GetEntityZones(BaseEntity entity)
        {
            if (configData.Options.useZoneManager)
            {
                if (entity is BasePlayer)
                {
                    return (string[])ZoneManager?.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                }
                else if (entity.IsValid())
                {
                    return (string[])ZoneManager?.Call("GetEntityZoneIDs", new object[] { entity });
                }
            }
            else if (configData.Options.useLiteZones)
            {
                if (entity.IsValid())
                {
                    return (string[])LiteZones?.Call("GetEntityZoneIDs", new object[] { entity });
                }
            }
            return null;
        }

        //private bool IsHumanNPC(BaseEntity player) => (bool)HumanNPC?.Call("IsHumanNPC", player as BasePlayer);
        private bool IsHumanNPC(BaseEntity player)
        {
            if (HumanNPC)
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
            if (hitInfo.Initiator is BaseHelicopter
               || (hitInfo.Initiator != null && (hitInfo.Initiator.ShortPrefabName.Equals("oilfireballsmall") || hitInfo.Initiator.ShortPrefabName.Equals("napalm"))))
            {
                return true;
            }

            else if (hitInfo.WeaponPrefab != null)
            {
                if (hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli") || hitInfo.WeaponPrefab.ShortPrefabName.Equals("rocket_heli_napalm"))
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
            if (turret != null)
            {
                weapon = hitInfo.Weapon;
                return true;
            }

            weapon = null;
            return false;
        }

        private bool PlayerOwnsItem(BasePlayer player, BaseEntity entity)
        {
            if (entity is BuildingBlock)
            {
                if (!configData.Options.HonorBuildingPrivilege) return true;

                BuildingManager.Building building = (entity as BuildingBlock).GetBuilding();

                if (building != null)
                {
                    var privs = building.GetDominatingBuildingPrivilege();
                    if (privs == null) return false;
                    foreach (var auth in privs.authorizedPlayers.Select(x => x.userid).ToArray())
                    {
                        if (entity.OwnerID == player.userID)
                        {
                            DoLog($"Player owns BuildingBlock", 2);
                            return true;
                        }
                        else if (player.userID == auth)
                        {
                            DoLog($"Player has privilege on BuildingBlock", 2);
                            return true;
                        }
                        //                        else if(configData.Options.HonorRelationships && IsFriend(auth, entity.OwnerID))
                        //                        {
                        //                              DoLog($"Player is friends with owner of BuildingBlock", 2);
                        //                            return true;
                        //                        }
                    }
                }
            }
            else
            {
                if (configData.Options.HonorRelationships)
                {
                    if (IsFriend(player.userID, entity.OwnerID))
                    {
                        DoLog($"Player is friends with owner of entity", 2);
                        return true;
                    }
                }
                else if (entity.OwnerID == player.userID)
                {
                    DoLog($"Player owns BuildingBlock", 2);
                    return true;
                }
            }
            return false;
        }

        private static bool GetBoolValue(string value)
        {
            if (value == null) return false;
            value = value.Trim().ToLower();
            switch (value)
            {
                case "t":
                case "true":
                case "1":
                case "yes":
                case "y":
                case "on":
                    return true;
                default:
                    return false;
            }
        }

        bool IsFriend(ulong playerid, ulong ownerid)
        {
            if (configData.Options.useFriends && Friends != null)
            {
                var fr = Friends?.CallHook("AreFriends", playerid, ownerid);
                if (fr != null && (bool)fr)
                {
                    return true;
                }
            }
            if (configData.Options.useClans && Clans != null)
            {
                string playerclan = (string)Clans?.CallHook("GetClanOf", playerid);
                string ownerclan = (string)Clans?.CallHook("GetClanOf", ownerid);
                if (playerclan == ownerclan && playerclan != null && ownerclan != null)
                {
                    return true;
                }
            }
            if (configData.Options.useTeams)
            {
                BasePlayer player = BasePlayer.FindByID(playerid);
                if (player.currentTeam != (long)0)
                {
                    RelationshipManager.PlayerTeam playerTeam = RelationshipManager.Instance.FindTeam(player.currentTeam);
                    if (playerTeam == null) return false;
                    if (playerTeam.members.Contains(ownerid))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        #endregion

        #region classes
        public class RealPVERuleSet
        {
            public bool damage;
            public List<string> except;
            public List<string> src_exclude;
            public List<string> tgt_exclude;
            public string zone;
            public string schedule;
            public bool enabled;
            public bool automated = false;
        }

        public class RealPVERule
        {
            public string description;
            public bool damage;
            public bool custom = true;
            public List<string> source;
            public List<string> target;
        }

        public class RealPVEEntities
        {
            public List<string> types;
            public bool custom = false;
        }

        private class RealPVEZoneMap
        {
            public List<string> map;
        }

        public static class UI
        {
            public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = color },
                            RectTransform = {AnchorMin = min, AnchorMax = max},
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }
            public static void Panel(ref CuiElementContainer container, string panel, string color, string min, string max, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    CursorEnabled = cursor
                },
                panel);
            }
            public static void Label(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { Color = color, FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = min, AnchorMax = max }
                },
                panel);

            }
            public static void Button(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    Text = { Text = text, FontSize = size, Align = align }
                },
                panel);
            }
            public static void Input(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Align = align,
                            CharsLimit = 30,
                            Color = color,
                            Command = command + text,
                            FontSize = size,
                            IsPassword = false,
                            Text = text
                        },
                        new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max },
                        new CuiNeedsCursorComponent()
                    }
                });
            }
            public static void Icon(ref CuiElementContainer container, string panel, string color, string imageurl, string min, string max)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Url = imageurl,
                            Sprite = "assets/content/textures/generic/fulltransparent.tga",
                            Color = color
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = min,
                            AnchorMax = max
                        }
                    }
                });
            }
            public static string Color(string hexColor, float alpha)
            {
                if (hexColor.StartsWith("#"))
                {
                    hexColor = hexColor.Substring(1);
                }
                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }
        #endregion

        #region load_defaults
        private void LoadDefaultRuleset(bool sql = false)
        {
            if (sql)
            {
                SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS rpve_rulesets", sqlConnection);
                ct = new SQLiteCommand("CREATE TABLE rpve_rulesets (name VARCHAR(255), damage INTEGER(1) DEFAULT 0, enabled INTEGER(1) DEFAULT 1, automated INTEGER(1) DEFAULT 0, zone VARCHAR(255), exception VARCHAR(255), src_exclude VARCHAR(255), tgt_exclude VARCHAR(255))", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_animal', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_animal', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_helicopter', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_minicopter', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_npc', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_building', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_resource', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_animal', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_npc', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'scrapcopter_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'trap_trap', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_player', null, null)", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_resource', null, null)", sqlConnection);

                return;
            }
            rpverulesets.Add("default", new RealPVERuleSet()
            {
                damage = false,
                zone = null,
                schedule = null,
                enabled = true,
                except = new List<string>() { "animal_player", "player_animal", "animal_animal", "player_helicopter", "player_minicopter", "player_scrapcopter", "player_npc", "npc_player", "player_building", "player_resource", "npcturret_player", "npcturret_animal", "npcturret_npc", "player_scrapcopter", "scrapcopter_player", "helicopter_player", "trap_trap" },
                src_exclude = new List<string>() { },
                tgt_exclude = new List<string>() { }
            });
        }

        // Default entity categories and types to consider
        private void LoadDefaultEntities(bool sql = false)
        {
            if (sql)
            {
                SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS rpve_entities", sqlConnection);
                ct = new SQLiteCommand("CREATE TABLE rpve_entities (name varchar(32), type varchar(32))", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'NPCPlayerApex')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'NPCPlayerApex')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'BradleyAPC')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'HumanNPC')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'BaseNpc')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'HTNPlayer')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Murderer')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Scientist')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Zombie')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'ResourceEntity')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'LootContainer')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'DroppedItemContainer')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'BaseCorpse')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'TeslaCoil')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'BearTrap')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'FlameTurret')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'Landmine')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'GunTrap')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'ReactiveTarget')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'spikes.floor')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'Landmine')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'BaseAnimalNPC')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Boar')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Bear')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Chicken')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Stag')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Wolf')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Horse')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('fire', 'BaseOven')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('fire', 'FireBall')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('player', 'BasePlayer')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('building', 'BuildingBlock')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('helicopter', 'BaseHeli')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('minicopter', 'Minicopter')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('scrapcopter', 'ScrapTransportHelicopter')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npcturret', 'NPCAutoTurret')", sqlConnection);

                return;
            }
            rpveentities.Add("npc", new RealPVEEntities() { types = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer", "Murderer", "Scientist", "Zombie" } });
            rpveentities.Add("player", new RealPVEEntities() { types = new List<string>() { "BasePlayer" } });
            rpveentities.Add("building", new RealPVEEntities() { types = new List<string>() { "BuildingBlock" } });
            rpveentities.Add("resource", new RealPVEEntities() { types = new List<string>() { "ResourceEntity", "LootContainer", "BaseCorpse" } });
            rpveentities.Add("trap", new RealPVEEntities() { types = new List<string>() { "TeslaCoil", "BearTrap", "FlameTurret", "Landmine", "GunTrap", "ReactiveTarget", "spikes.floor", "Landmine" } });
            rpveentities.Add("animal", new RealPVEEntities() { types = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" } });
            rpveentities.Add("helicopter", new RealPVEEntities() { types = new List<string>() { "BaseHeli" } });
            rpveentities.Add("minicopter", new RealPVEEntities() { types = new List<string>() { "Minicopter" } });
            rpveentities.Add("scrapcopter", new RealPVEEntities() { types = new List<string>() { "ScrapTransportHelicopter" } });
            rpveentities.Add("highwall", new RealPVEEntities() { types = new List<string>() { "wall.external.high.stone", "wall.external.high.wood", "gates.external.high.wood", "gates.external.high.stone" } });
            rpveentities.Add("npcturret", new RealPVEEntities() { types = new List<string>() { "NPCAutoTurret" } });
            rpveentities.Add("fire", new RealPVEEntities() { types = new List<string>() { "BaseOven", "FireBall" } });
        }

        // Default rules which can be applied
        private void LoadDefaultRules(bool sql = false)
        {
            if (sql)
            {
                SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS rpve_rules", sqlConnection);
                ct = new SQLiteCommand("CREATE TABLE rpve_rules (name varchar(32),description varchar(255),damage INTEGER(1) DEFAULT 1,custom INTEGER(1) DEFAULT 0,source VARCHAR(32),target VARCHAR(32));", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npc_player', 'NPC can damage player', 1, 0, 'npc', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_npc', 'Player can damage NPC', 1, 0, 'player', 'npc')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_player', 'Player can damage Player', 1, 0, 'player', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_building', 'Player can damage Building', 1, 0, 'player', 'building')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_resource', 'Player can damage Resource', 1, 0, 'player', 'resource')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('players_traps', 'Player can damage Trap', 1, 0, 'player', 'trap')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('traps_players', 'Trap can damage Player', 1, 0, 'trap', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_animal', 'Player can damage Animal', 1, 0, 'player', 'animal')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('animal_player', 'Animal can damage Player', 1, 0, 'animal', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('animal_animal', 'Animal can damage Animal', 1, 0, 'animal', 'animal')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('helicopter_player', 'Helicopter can damage Player', 1, 0, 'helicopter', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('helicopter_building', 'Helicopter can damage Building', 1, 0, 'helicopter', 'building')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_minicopter', 'Player can damage Minicopter', 1, 0, 'player', 'minicopter')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('minicopter_player', 'Minicopter can damage Player', 1, 0, 'minicopter', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_scrapcopter', 'Player can damage Scrapcopter', 1, 0, 'player', 'scrapcopter')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('scrapcopter_player', 'Scrapcopter can damage Player', 1, 0, 'scrapcopter', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_helicopter', 'Player can damage Helicopter', 1, 0, 'player', 'helicopter')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('highwalls_player', 'Highwall can damage Player', 1, 0, 'highwall', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_player', 'NPCAutoTurret can damage Player', 1, 0, 'npcturret', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_animal', 'NPCAutoTurret can damage Animal', 1, 0, 'npcturret', 'animal')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_npc', 'NPCAutoTurret can damage NPC', 1, 0, 'npcturret', 'npc')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('fire_player', 'Fire can damage Player', 1, 0, 'fire', 'player')", sqlConnection);
                ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('fire_resource', 'Fire can damage Resource', 1, 0, 'fire', 'resource')", sqlConnection);

                return;
            }
            rpverules.Add("npc_player", new RealPVERule() { description = "npc can damage player", damage = true, custom = false, source = rpveentities["npc"].types, target = rpveentities["player"].types });
            rpverules.Add("player_npc", new RealPVERule() { description = "Player can damage npc", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["npc"].types });
            rpverules.Add("player_player", new RealPVERule() { description = "Player can damage player", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["player"].types });
            rpverules.Add("player_building", new RealPVERule() { description = "Player can damage building", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["building"].types });
            rpverules.Add("player_resource", new RealPVERule() { description = "Player can damage resource", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["resource"].types });
            rpverules.Add("players_traps", new RealPVERule() { description = "Player can damage trap", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["trap"].types });
            rpverules.Add("traps_players", new RealPVERule() { description = "Trap can damage player", damage = true, custom = false, source = rpveentities["trap"].types, target = rpveentities["player"].types });
            rpverules.Add("player_animal", new RealPVERule() { description = "Player can damage animal", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["animal"].types });
            rpverules.Add("animal_player", new RealPVERule() { description = "Animal can damage player", damage = true, custom = false, source = rpveentities["animal"].types, target = rpveentities["player"].types });
            rpverules.Add("animal_animal", new RealPVERule() { description = "Animal can damage animal", damage = true, custom = false, source = rpveentities["animal"].types, target = rpveentities["animal"].types });
            rpverules.Add("helicopter_player", new RealPVERule() { description = "Helicopter can damage player", damage = true, custom = false, source = rpveentities["helicopter"].types, target = rpveentities["player"].types });
            rpverules.Add("helicopter_building", new RealPVERule() { description = "Helicopter can damage building", damage = true, custom = false, source = rpveentities["helicopter"].types, target = rpveentities["building"].types });
            rpverules.Add("player_minicopter", new RealPVERule() { description = "Player can damage minicopter", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["minicopter"].types });
            rpverules.Add("minicopter_player", new RealPVERule() { description = "Minicopter can damage player", damage = true, custom = false, source = rpveentities["minicopter"].types, target = rpveentities["player"].types });
            rpverules.Add("player_scrapcopter", new RealPVERule() { description = "Player can damage scrapcopter", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["scrapcopter"].types });
            rpverules.Add("scrapcopter_player", new RealPVERule() { description = "Scrapcopter can damage player", damage = true, custom = false, source = rpveentities["scrapcopter"].types, target = rpveentities["player"].types });
            rpverules.Add("player_helicopter", new RealPVERule() { description = "Player can damage helicopter", damage = true, custom = false, source = rpveentities["player"].types, target = rpveentities["helicopter"].types });
            rpverules.Add("highwalls_player", new RealPVERule() { description = "Highwall can damage player", damage = true, custom = false, source = rpveentities["highwall"].types, target = rpveentities["player"].types });
            rpverules.Add("npcturret_player", new RealPVERule() { description = "NPCAutoTurret can damage player", damage = true, custom = false, source = rpveentities["npcturret"].types, target = rpveentities["player"].types });
            rpverules.Add("npcturret_animal", new RealPVERule() { description = "NPCAutoTurret can damage animal", damage = true, custom = false, source = rpveentities["npcturret"].types, target = rpveentities["animal"].types });
            rpverules.Add("npcturret_npc", new RealPVERule() { description = "NPCAutoTurret can damage npc", damage = true, custom = false, source = rpveentities["npcturret"].types, target = rpveentities["npc"].types });
            rpverules.Add("fire_player", new RealPVERule() { description = "Fire can damage player", damage = true, custom = false, source = rpveentities["fire"].types, target = rpveentities["player"].types });
        }
        #endregion
    }
}
