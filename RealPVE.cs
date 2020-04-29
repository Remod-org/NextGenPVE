using Oxide.Core;
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
// Finish sqlite rework
// Add the actual schedule handling...
// Finish work on custom rule editor gui (src/target)
// Sanity checking for overlapping rule/zone combinations.  Schedule may have impact.
// Add setting global flags

namespace Oxide.Plugins
{
    [Info("Real PVE", "RFC1920", "1.0.15")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class RealPVE : RustPlugin
    {
        #region vars
        Dictionary<string, RealPVERule> custom_rules = new Dictionary<string, RealPVERule>();
        // Ruleset to multiple zones
        Dictionary<string, RealPVEZoneMap> rpvezonemaps = new Dictionary<string, RealPVEZoneMap>();

        private const string permRealPVEUse = "realpve.use";
        private const string permRealPVEAdmin = "realpve.admin";
        private const string permRealPVEGod = "realpve.god";
        private ConfigData configData;

        SQLiteConnection sqlConnection;
        private string connStr;

        [PluginReference]
        private readonly Plugin ZoneManager, LiteZones, HumanNPC, Friends, Clans, RustIO;

        private readonly string logfilename = "log";
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

            Puts("Creating database connection for main thread.");
            connStr = $"Data Source={Interface.Oxide.DataDirectory}{Path.DirectorySeparatorChar}{Name}{Path.DirectorySeparatorChar}realpve.db;";
            sqlConnection = new SQLiteConnection(connStr);
            Puts("Opening...");
            sqlConnection.Open();

            LoadData();

            AddCovalenceCommand("pverule", "CmdRealPVEGUI");
            AddCovalenceCommand("pveenable", "CmdRealPVEenable");
            AddCovalenceCommand("pvelog", "CmdRealPVElog");

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
            sqlConnection.Close();
        }

        private void LoadData()
        {
            bool found = false;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                c.Open();
                using (SQLiteCommand r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_entities'", c))
                {
                    using (SQLiteDataReader rentry = r.ExecuteReader())
                    {
                        while (rentry.Read()) { found = true; }
                    }
                }
            }
            if (!found) LoadDefaultEntities();

            found = false;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_rules'", c))
                {
                    using (SQLiteDataReader rentry = r.ExecuteReader())
                    {
                        while (rentry.Read()) { found = true; }
                    }
                }
            }
            if (!found) LoadDefaultRules();

            found = false;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand r = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name='rpve_rulesets'", c))
                {
                    using (SQLiteDataReader rentry = r.ExecuteReader())
                    {
                        while (rentry.Read()) { found = true; }
                    }
                }
            }
            if (!found) LoadDefaultRuleset();
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

        private bool TogglePVERuleset(string rulesetname, bool on = true)
        {
            string enable = "0";
            if (on) enable = "1";
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand cmd = new SQLiteCommand($"UPDATE rpve_rulesets SET enabled='{enable}", c))
                {
                    cmd.ExecuteNonQuery();
                }
            }
            return true;
        }

        private bool AddOrUpdateMapping(string key, string rulesetname)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (rulesetname == null) return false;

            DoLog($"AddOrUpdateMapping called for ruleset: {rulesetname}, zone: {key}", 0);
            bool foundrs = false;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand au = new SQLiteCommand($"SELECT DISTINCT enable FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SQLiteDataReader ar = au.ExecuteReader())
                    {
                        while (ar.Read())
                        {
                            foundrs = true;
                        }
                    }
                }
            }
            if(foundrs)
            {
                if (rpvezonemaps.ContainsKey(rulesetname) && !rpvezonemaps[rulesetname].map.Contains(key))
                {
                    rpvezonemaps[rulesetname].map.Add(key);
                }
                else
                {
                    rpvezonemaps.Add(rulesetname, new RealPVEZoneMap() { map = new List<string>() { key } });
                }
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    DoLog($"UPDATE rpve_rulesets SET zone='lookup' WHERE rulesetname='{rulesetname}'");
                    using (SQLiteCommand cmd = new SQLiteCommand($"UPDATE rpve_rulesets SET zone='lookup' WHERE rulesetname='{rulesetname}'", c))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                return true;
            }
            else
            {
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    DoLog($"INSERT INTO rpve_rulesets VALUES('{rulesetname}', '1', '1', '1', 'lookup', '', '', '', '')");
                    using (SQLiteCommand cmd = new SQLiteCommand($"INSERT INTO rpve_rulesets VALUES('{rulesetname}', '1', '1', '1', 'lookup', '', '', '', '')", c))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                if (rpvezonemaps.ContainsKey(rulesetname)) rpvezonemaps.Remove(rulesetname);
                rpvezonemaps.Add(rulesetname, new RealPVEZoneMap() { map = new List<string>() { key } });
                return true;
            }
        }

        private bool RemoveMapping(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            List<string> foundrs = new List<string>();

            DoLog($"RemoveMapping called for zone: {key}", 0);

            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                Puts($"SELECT name FROM rpve_rulesets WHERE zone='{key}'");
                using (SQLiteCommand rm = new SQLiteCommand($"SELECT name FROM rpve_rulesets WHERE zone='{key}'", c))
                {
                    using (SQLiteDataReader rd = rm.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string rn = rd.GetString(0);
                            foundrs.Add(rn);
                            rpvezonemaps.Remove(rn);
                        }
                    }
                }
            }

            if (foundrs.Count > 0)
            {
                foreach (var found in foundrs)
                {
                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                    {
                        c.Open();
                        using (SQLiteCommand cmd = new SQLiteCommand($"UPDATE rpve_rulesets SET zone='0' WHERE name='{found}'", c))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
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
            string rulesetzone = "";
            bool hasBP = false;

            // Special case since HumanNPC contains a BasePlayer object
            if (stype == "BasePlayer" && HumanNPC && IsHumanNPC(source)) stype = "HumanNPC";
            if (ttype == "BasePlayer" && HumanNPC && IsHumanNPC(target)) ttype = "HumanNPC";

            //var turret = source.Weapon?.GetComponentInParent<AutoTurret>();

            if (stype == "BasePlayer" && ttype == "BuildingBlock")
            {
                if (PlayerOwnsItem(source as BasePlayer, target)) hasBP = true;
            }

            //bool zmatch = false;
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

            bool foundmatch = false;
            bool damage = true;
            bool enabled = false;
            string rulesetname = null;

            string src = null; string tgt = null;
            using (SQLiteCommand findIt = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{stype}'", sqlConnection))
                {
                using (SQLiteDataReader readMe = findIt.ExecuteReader())
                {
                    while (readMe.Read())
                    {
                        src = readMe.GetString(0);
                        break;
                    }
                }
            }

            using (SQLiteCommand findIt = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{ttype}'", sqlConnection))
            {
                using (SQLiteDataReader readMe = findIt.ExecuteReader())
                {
                    while (readMe.Read())
                    {
                        tgt = readMe.GetString(0);
                        break;
                    }
                }
            }

            using (SQLiteCommand findIt = new SQLiteCommand("SELECT DISTINCT name, zone, damage, enabled FROM rpve_rulesets", sqlConnection))
            {
                using (SQLiteDataReader readMe = findIt.ExecuteReader())
                {
                    while (readMe.Read())
                    {
                        rulesetname = readMe.GetString(0);
                        rulesetzone = readMe.GetString(1);
                        damage = readMe.GetBoolean(2);
                        enabled = readMe.GetBoolean(3);

                        DoLog($"Checking {rulesetname} for {stype} attacking {ttype}");
                        if (src != null && tgt != null)
                        {
                            DoLog($"Found {stype} attacking {ttype}.  Checking ruleset {rulesetname}");
                            int en = enabled ? 1 : 0;
                            using (SQLiteCommand rq = new SQLiteCommand($"SELECT enabled, src_exclude, tgt_exclude FROM rpve_rulesets WHERE name='{rulesetname}' AND enabled='{en}' AND exception='{src}_{tgt}'", sqlConnection))
                            {
                                using (SQLiteDataReader entry = rq.ExecuteReader())
                                {
                                    while (entry.Read())
                                    {
                                        // source and target exist - verify that they are not excluded
                                        DoLog($"Found exception match for {stype} attacking {ttype}");
                                        string foundsrc = entry.GetValue(1).ToString();
                                        string foundtgt = entry.GetValue(2).ToString();
                                        if (foundsrc.Contains(stype))
                                        {
                                            DoLog($"Exclusion for {stype}");
                                            foundmatch = false;
                                            break;
                                        }
                                        else if (foundtgt.Contains(ttype))
                                        {
                                            DoLog($"Exclusion for {ttype}");
                                            foundmatch = false;
                                            break;
                                        }
                                        else
                                        {
                                            DoLog($"No exclusions for {stype} to {ttype}");
                                            foundmatch = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if (hasBP)
            {
                DoLog($"Player has building privilege and is attacking a BuildingBlock");
                return true;
            }
            // These zone checks currently override all of the work above and are utter shit
            if (rulesetzone == "lookup" && rpvezonemaps.ContainsKey(rulesetname))
            {
                if (!rpvezonemaps[rulesetname].map.Contains(zone))
                {
                    DoLog($"Skipping check due to zone {zone} mismatch");
                    return false;
                }
            }
            else if (zone != "default" && zone != rulesetzone)
            {
                DoLog($"Skipping check due to zone {zone} mismatch");
                return false;
            }

            if (foundmatch)
            {
                DoLog($"Ruleset exception: Setting damage to {(!damage).ToString()}");
                return !damage;
            }
            else
            {
                DoLog($"Ruleset match: Setting damage to {damage.ToString()}");
                return damage;
            }

            //DoLog($"NO RULESET MATCH!");
        }

        private void DoLog(string message, int indent = 0)
        {
            if (!enabled) return;
            if (message.Contains("Turret")) return; // Log volume FIXME
            if (dolog) LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
        }
        #endregion

        #region Commands
        [Command("pveenable")]
        private void CmdRealPVEenable(IPlayer player, string command, string[] args)
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
        private void CmdRealPVElog(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission(permRealPVEAdmin)) { Message(player, "notauthorized"); return; }

            dolog = !dolog;
            Message(player, "logging", dolog.ToString());
        }

        [Command("pverule")]
        private void CmdRealPVEGUI(IPlayer iplayer, string command, string[] args)
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
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand defload = new SQLiteCommand("DELETE FROM rpve_rulesets WHERE name='default'", c))
                                        {
                                            defload.ExecuteNonQuery();
                                        }
                                    }
                                    LoadDefaultRuleset(false);
                                    break;
                                case "enable":
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand en = new SQLiteCommand($"UPDATE rpve_rulesets SET enabled='{newval}' WHERE name='{rs}'", c))
                                        {
                                            en.ExecuteNonQuery();
                                        }
                                    }
                                    break;
                                case "damage":
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand dm = new SQLiteCommand($"UPDATE rpve_rulesets SET damage='{newval}' WHERE name='{rs}'", c))
                                        {
                                            dm.ExecuteNonQuery();
                                        }
                                    }
                                    break;
                                case "name":
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand nm = new SQLiteCommand($"UPDATE rpve_rulesets SET name='{newval}' WHERE name='{rs}'", c))
                                        {
                                            nm.ExecuteNonQuery();
                                            rs = newval;
                                        }
                                    }
                                    break;
                                case "zone":
                                    if (newval == "delete")
                                    {
                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SQLiteCommand zup = new SQLiteCommand($"UPDATE rpve_rulesets SET zone='' WHERE name='{rs}'", c))
                                            {
                                                zup.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SQLiteCommand zu = new SQLiteCommand($"UPDATE rpve_rulesets SET zone='{newval}' WHERE name='{rs}'", c))
                                            {
                                                zu.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                    break;
                                case "schedule":
                                    if (newval == "0")
                                    {
                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SQLiteCommand use = new SQLiteCommand($"UPDATE rpve_rulesets SET schedule='' WHERE name='{rs}'", c))
                                            {
                                                use.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SQLiteCommand us = new SQLiteCommand($"UPDATE rpve_rulesets SET schedule='{newval}' WHERE name='{rs}'", c))
                                            {
                                                us.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                    break;
                                case "except":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} except {pverule.Key} add
                                    //pverule editruleset noturret except npcturret_animal delete
                                    switch (args[4])
                                    {
                                        case "add":
                                            Puts($"SELECT exception FROM rpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'");
                                            bool isNew = true;
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand ce = new SQLiteCommand($"SELECT exception FROM rpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'", c))
                                                {
                                                    using (SQLiteDataReader re = ce.ExecuteReader())
                                                    {
                                                        while (re.Read())
                                                        {
                                                            isNew = false;
                                                        }
                                                    }
                                                }
                                            }
                                            if (isNew)
                                            {
                                                Puts($"INSERT INTO rpve_rulesets VALUES('{rs}', 1, 1, 0, '', '{args[3]}', '', '')");
                                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                {
                                                    c.Open();
                                                    using (SQLiteCommand ae = new SQLiteCommand($"INSERT INTO rpve_rulesets VALUES('{rs}', 1, 1, 0, '', '{args[3]}', '', '', 0)", c))
                                                    {
                                                        ae.ExecuteNonQuery();
                                                    }
                                                }
                                            }
                                            break;
                                        case "delete":
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand ad = new SQLiteCommand($"DELETE FROM rpve_rulesets WHERE name='{rs}' AND exception='{args[3]}'", c))
                                                {
                                                    ad.ExecuteNonQuery();
                                                }
                                            }
                                            break;
                                    }
                                    break;
                                case "src_exclude":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} src_exclude Horse add
                                    switch (args[4])
                                    {
                                        case "add":
                                            string etype = null;
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand aee = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{newval}'", c))
                                                {
                                                    using (SQLiteDataReader aed = aee.ExecuteReader())
                                                    {
                                                        while (aed.Read())
                                                        {
                                                            etype = aed.GetString(0) ?? "";
                                                            Puts($"Found type {etype} of {newval}");
                                                        }
                                                    }
                                                }
                                            }

                                            if(etype != "")
                                            {
                                                string exception = ""; string oldsrc = "";
                                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                {
                                                    c.Open();
                                                    using (SQLiteCommand cex = new SQLiteCommand($"SELECT exception, src_exclude FROM rpve_rulesets WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                    {
                                                        SQLiteDataReader rex = cex.ExecuteReader();
                                                        while (rex.Read())
                                                        {
                                                            exception = rex.GetValue(0).ToString();
                                                            oldsrc = rex.GetValue(1).ToString();
                                                            Puts($"Found existing exception '{exception}' and src_exclude of '{oldsrc}'");
                                                        }
                                                    }
                                                }
                                                if (exception != "" && oldsrc != "")
                                                {
                                                    if (!oldsrc.Contains(newval))
                                                    {
                                                        string newsrc = string.Join(",", oldsrc, newval);
                                                        Puts($"Adding src_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}, oldsrc = '{oldsrc}'");
                                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                        {
                                                            c.Open();
                                                            Puts($"UPDATE rpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND exception LIKE '{etype}_%'");
                                                            using (SQLiteCommand aes = new SQLiteCommand($"UPDATE rpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                            {
                                                                aes.ExecuteNonQuery();
                                                            }
                                                        }
                                                    }
                                                }
                                                else if(exception != "")
                                                {
                                                    if (!oldsrc.Contains(newval))
                                                    {
                                                        Puts($"Updating src_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}, oldsrc = '{oldsrc}'");
                                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                        {
                                                            c.Open();
                                                            Puts($"UPDATE rpve_rulesets SET src_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '{etype}_%'");
                                                            using (SQLiteCommand aes = new SQLiteCommand($"UPDATE rpve_rulesets SET src_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '{etype}_%'", c))
                                                            {
                                                                aes.ExecuteNonQuery();
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            break;
                                        case "delete":
                                            bool foundSrc = false;
                                            string src_excl = "";
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand findSrc = new SQLiteCommand($"SELECT DISTINCT src_exclude FROM rpve_rulesets WHERE src_exclude LIKE '%{newval}%'", c))
                                                {
                                                    using (SQLiteDataReader fs = findSrc.ExecuteReader())
                                                    {
                                                        while (fs.Read())
                                                        {
                                                            foundSrc = true;
                                                            src_excl = fs.GetString(0) ?? "";
                                                            Puts($"Found src_exclude {src_excl}");
                                                        }
                                                    }
                                                }
                                            }

                                            if (foundSrc)
                                            {
                                                string newsrc = src_excl.Replace(newval, "");
                                                newsrc.Replace(",,", ",");
                                                if (newsrc.Trim() == "," || newsrc.Trim() == ",,") newsrc = "";
                                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                {
                                                    c.Open();
                                                    Puts($"UPDATE rpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND src_exclude='{src_excl}'");
                                                    using (SQLiteCommand ads = new SQLiteCommand($"UPDATE rpve_rulesets SET src_exclude='{newsrc}' WHERE name='{rs}' AND src_exclude='{src_excl}'", c))
                                                    {
                                                        ads.ExecuteNonQuery();
                                                    }
                                                }
                                            }
                                            break;
                                    }
                                    break;
                                case "tgt_exclude":
                                    if (args.Length < 5) return;
                                    //pverule editruleset {rulesetname} tgt_exclude Horse delete
                                    switch (args[4])
                                    {
                                        case "add":
                                            string etype = null;
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand aee = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_entities WHERE type='{newval}'", c))
                                                {
                                                    using (SQLiteDataReader aed = aee.ExecuteReader())
                                                    {
                                                        while (aed.Read())
                                                        {
                                                            etype = aed.GetString(0) ?? "";
                                                            Puts($"Found type {etype} of {newval}");
                                                        }
                                                    }
                                                }
                                            }

                                            if(etype != "")
                                            {
                                                string exception = ""; string oldtgt = "";
                                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                {
                                                    c.Open();
                                                    using (SQLiteCommand cex = new SQLiteCommand($"SELECT exception, tgt_exclude FROM rpve_rulesets WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                    {
                                                        SQLiteDataReader rex = cex.ExecuteReader();
                                                        while (rex.Read())
                                                        {
                                                            exception = rex.GetValue(0).ToString();
                                                            oldtgt = rex.GetValue(1).ToString();
                                                            Puts($"Found existing exception {exception} and tgt_exclude of {oldtgt}");
                                                        }
                                                    }
                                                }
                                                if (exception != "" && oldtgt != "")
                                                {
                                                    if (!oldtgt.Contains(newval))
                                                    {
                                                        string newtgt = string.Join(",", oldtgt, newval);
                                                        Puts($"Adding tgt_exclude of '{newtgt}' to ruleset: '{rs}', type: '{etype}' - Input was {newval}");
                                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                        {
                                                            c.Open();
                                                            Puts($"UPDATE rpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND exception LIKE '%_{etype}'");
                                                            using (SQLiteCommand aes = new SQLiteCommand($"UPDATE rpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                            {
                                                                aes.ExecuteNonQuery();
                                                            }
                                                        }
                                                    }
                                                }
                                                else if(exception != "")
                                                {
                                                    if (!oldtgt.Contains(newval))
                                                    {
                                                        Puts($"Updating tgt_exclude of '{newval}' to ruleset: '{rs}' type: '{etype}' - Input was {newval}");
                                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                        {
                                                            c.Open();
                                                            Puts($"UPDATE rpve_rulesets SET tgt_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '%_{etype}'");
                                                            using (SQLiteCommand aes = new SQLiteCommand($"UPDATE rpve_rulesets SET tgt_exclude='{newval}' WHERE name='{rs}' AND exception LIKE '%_{etype}'", c))
                                                            {
                                                                aes.ExecuteNonQuery();
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            break;
                                        case "delete":
                                            bool foundTgt = false;
                                            string tgt_excl = "";
                                            using (SQLiteConnection c = new SQLiteConnection(connStr))
                                            {
                                                c.Open();
                                                using (SQLiteCommand findTgt = new SQLiteCommand($"SELECT DISTINCT tgt_exclude FROM rpve_rulesets WHERE tgt_exclude LIKE '%{newval}%'", c))
                                                {
                                                    using (SQLiteDataReader ft = findTgt.ExecuteReader())
                                                    {
                                                        while (ft.Read())
                                                        {
                                                            foundTgt = true;
                                                            tgt_excl = ft.GetString(0) ?? "";
                                                            Puts($"Found tgt_exclude {tgt_excl}");
                                                        }
                                                    }
                                                }
                                            }

                                            if (foundTgt)
                                            {
                                                string newtgt = tgt_excl.Replace(newval, "");
                                                newtgt.Replace(",,", ",");
                                                if (newtgt.Trim() == "," || newtgt.Trim() == ",,") newtgt = "";
                                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                                {
                                                    c.Open();
                                                    Puts($"UPDATE rpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND tgt_exclude='{tgt_excl}'");
                                                    using (SQLiteCommand ads = new SQLiteCommand($"UPDATE rpve_rulesets SET tgt_exclude='{newtgt}' WHERE name='{rs}' AND tgt_exclude='{tgt_excl}'", c))
                                                    {
                                                        ads.ExecuteNonQuery();
                                                    }
                                                }
                                            }
                                            break;
                                    }
                                    break;
                            }
                            GUIRuleSets(player);
                            GUIRulesetEditor(player, rs);
                        }
                        //pverule editruleset {rulesetname} delete
                        //pverule editruleset {rulesetname} zone
                        //pverule editruleset new src_exclude add
                        // This is where we either delete or load the dialog to edit values.
                        else if (args.Length > 2)
                        {
                            switch (args[2])
                            {
                                case "delete":
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand drs = new SQLiteCommand($"DELETE FROM rpve_rulesets WHERE name='{args[1]}'", c))
                                        {
                                            drs.ExecuteNonQuery();
                                        }
                                    }
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
                                    while (true)
                                    {
                                        using (SQLiteConnection c = new SQLiteConnection(connStr))
                                        {
                                            c.Open();
                                            using (SQLiteCommand checkrs = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_rulesets WHERE name='{clone}'", c))
                                            {
                                                using (SQLiteDataReader crs = checkrs.ExecuteReader())
                                                {
                                                    while (crs.Read())
                                                    {
                                                        id++;
                                                        if (id > 4) break;
                                                        clone = oldname + id.ToString();
                                                    }
                                                }
                                            }
                                        }
                                        break;
                                    }
                                    Puts($"Creating clone {clone}");
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand newrs = new SQLiteCommand($"INSERT INTO rpve_rulesets (name, damage, enabled, automated, zone, exception, src_exclude, tgt_exclude, schedule) SELECT '{clone}', damage, enabled, automated, zone, exception, src_exclude, tgt_exclude, schedule FROM rpve_rulesets WHERE name='{oldname}'", c))
                                        {
                                            newrs.ExecuteNonQuery();
                                        }
                                    }
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
                                newname = "new";
                                while (true)
                                {
                                    using (SQLiteConnection c = new SQLiteConnection(connStr))
                                    {
                                        c.Open();
                                        using (SQLiteCommand addrs = new SQLiteCommand($"SELECT DISTINCT name FROM rpve_rulesets WHERE name='{newname}'", c))
                                        {
                                            using (SQLiteDataReader addcrsd = addrs.ExecuteReader())
                                            {
                                                while (addcrsd.Read())
                                                {
                                                    id++;
                                                    if (id > 4) break;
                                                    newname = "new" + id.ToString();
                                                }
                                            }
                                        }
                                    }
                                    break;
                                }
                                Puts($"Creating new ruleset {newname}");
                                using (SQLiteConnection c = new SQLiteConnection(connStr))
                                {
                                    c.Open();
                                    using (SQLiteCommand newrs = new SQLiteCommand($"INSERT INTO rpve_rulesets VALUES ('{newname}', 0, 1, 0, 0, '', '', '', 0)", c))
                                    {
                                        newrs.ExecuteNonQuery();
                                    }
                                }
                                GUIRuleSets(player);
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

            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT name, automated from rpve_rulesets", c))
                {
                    using (SQLiteDataReader rsread = getrs.ExecuteReader())
                    {
                        while (rsread.Read())
                        {
                            string rsname = rsread.GetString(0);
                            bool automated = rsread.GetBoolean(1);
                            if (row > 10)
                            {
                                row = 0;
                                col++;
                            }
                            pb = GetButtonPositionP(row, col);
                            string rColor = "#d85540";
                            if (automated) rColor = "#5540d8";

                            UI.Button(ref container, RPVERULELIST, UI.Color(rColor, 1f), rsname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rsname}");
                            row++;
                        }
                    }
                }
            }

            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, RPVERULELIST, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset add");

            CuiHelper.AddUi(player, container);
        }

        private void GUIRulesetEditor(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVEEDITRULESET);
            string rulename = rulesetname;
            bool isEnabled = false;
            bool damage = false;
            string schedule = null;
            string zone = null;

            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT automated, enabled, damage, schedule, zone from rpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SQLiteDataReader rsread = getrs.ExecuteReader())
                    {
                        while (rsread.Read())
                        {
                            if (rsread.GetBoolean(0)) rulename += " (" + Lang("automated") + ")";
                            isEnabled = rsread.GetBoolean(1);
                            damage = rsread.GetBoolean(2);
                            zone = rsread.GetString(4);
                            try
                            {
                                schedule = rsread.GetString(3) ?? "0";
                            }
                            catch
                            {
                                schedule = "0";
                            }
                        }
                    }
                }
            }

            CuiElementContainer container = UI.Container(RPVEEDITRULESET, UI.Color("3b3b3b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("realpveruleset") + ": " + rulename, 24, "0.15 0.92", "0.7 1");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedulenotworking"), 12, "0.3 0.05", "0.7 0.08");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            UI.Button(ref container, RPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("clone"), 12, "0.63 0.95", "0.69 0.98", $"pverule editruleset {rulesetname} clone");
            if (isEnabled)
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
            int hdrcol = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, hdrcol);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("defaultdamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row += 2;
            pb = GetButtonPositionP(row, hdrcol);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("zone"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row += 2;
            pb = GetButtonPositionP(row, hdrcol);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedule"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row = 0; hdrcol++;
            pb = GetButtonPositionP(row, hdrcol);
            string de = Lang("block"); if (!damage) de = Lang("allow");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), de, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row++;
            hdrcol = 0;

            pb = GetButtonPositionP(row, hdrcol);
            if (rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if (damage)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("true"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 0");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(dicolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 1");
            }

            // Exceptions (block/allow)
            col = 1;
            int numExceptions = 0;

            Puts($"SELECT DISTINCT exception FROM rpve_rulesets WHERE name='{rulesetname}' ORDER BY exception");
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT exception FROM rpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
                {
                    using (SQLiteDataReader rsread = getrs.ExecuteReader())
                    {
                        while (rsread.Read())
                        {
                            string except = rsread.GetValue(0).ToString() ?? null;
                            Puts($"Found exception: {except}");
                            if (except == "") continue;
                            if (row > 11)
                            {
                                row = 1;
                                col++;
                            }
                            numExceptions++;
                            pb = GetButtonPositionP(row, col);
                            UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), except, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
                            row++;
                        }
                    }
                }
            }

            if (numExceptions < 1)
            {
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
            }
            else
            {
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }

            hdrcol += 2; row = 0;
            if (numExceptions> 11) hdrcol++;
            pb = GetButtonPositionP(row, hdrcol);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("source"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            hdrcol++;
            pb = GetButtonPositionP(row, hdrcol);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude") + " " + Lang("target"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            // Source exclusions from exceptions above
            col++; row = 1;
            bool noExclusions = true;
            if (numExceptions > 0) // Cannot exclude from exceptions that do not exist
            {
                Puts($"SELECT DISTINCT src_exclude FROM rpve_rulesets WHERE name='{rulesetname}'");
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT src_exclude FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                    {
                        using (SQLiteDataReader rsread = getrs.ExecuteReader())
                        {
                            while (rsread.Read())
                            {
                                string exclude = rsread.GetValue(0).ToString();
                                if (exclude == "") continue;
                                noExclusions = false;
                                if (row > 11)
                                {
                                    row = 0;
                                    col++;
                                }
                                noExclusions = false;
                                string[] excl = exclude.Split(',');
                                if (excl.Length > 1)
                                {
                                    foreach (string ex in excl)
                                    {
                                        Puts($"Adding button for existing src_exclude of {ex}");
                                        pb = GetButtonPositionP(row, col);
                                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), ex, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                                        row++;
                                    }
                                }
                                else
                                {
                                    Puts($"Adding button for existing src_exclude of {exclude}");
                                    pb = GetButtonPositionP(row, col);
                                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
                                    row++;
                                }
                            }
                        }
                    }
                }
            }
            if (noExclusions && numExceptions > 0)
            {
                row = 1;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12,  $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude");
            }

            // Target exclusions from exceptions above
            col++; row = 1;
            noExclusions = true;
            if (numExceptions > 0) // Cannot exclude from exceptions that do not exist
            {
                using (SQLiteConnection c = new SQLiteConnection(connStr))
                {
                    c.Open();
                    using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT tgt_exclude FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                    {
                        using (SQLiteDataReader rsread = getrs.ExecuteReader())
                        {
                            while (rsread.Read())
                            {
                                string exclude = rsread.GetValue(0).ToString();
                                if (exclude == "") continue;
                                noExclusions = false;
                                if (row > 11)
                                {
                                    row = 0;
                                    col++;
                                }
                                noExclusions = false;
                                string[] excl = exclude.Split(',');
                                if (excl.Length > 1)
                                {
                                    foreach (string ex in excl)
                                    {
                                        Puts($"Adding button for existing tgt_exclude of {ex}");
                                        pb = GetButtonPositionP(row, col);
                                        UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), ex, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                                        row++;
                                    }
                                }
                                else
                                {
                                    Puts($"Adding button for existing tgt_exclude of {exclude}");
                                    pb = GetButtonPositionP(row, col);
                                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
                                }
                                row++;
                            }
                        }
                    }
                }
            }

            if (noExclusions && numExceptions > 0)
            {
                row = 1;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12,  $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude");
            }

            col = 0; row = 3;
            pb = GetButtonPositionP(row, col);
            if (rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if (zone == "lookup")
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("lookup"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            else if (zone != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), zone, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
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
            if (schedule != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
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

            List<string> exc = new List<string>();
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand crs = new SQLiteCommand($"SELECT exception from rpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
                {
                    using (SQLiteDataReader crd = crs.ExecuteReader())
                    {
                        while (crd.Read())
                        {
                            exc.Add(crd.GetString(0));
                        }
                    }
                }
            }

            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand sr = new SQLiteCommand($"SELECT DISTINCT name, custom from rpve_rules ORDER BY name", c))
                {
                    using (SQLiteDataReader rr = sr.ExecuteReader())
                    {
                        float[] pb = GetButtonPositionP(row, col);
                        while (rr.Read())
                        {
                            string rulename = rr.GetString(0);
                            bool custom = rr.GetBoolean(1);
                            if (row > 10)
                            {
                                row = 0;
                                col++;
                            }
                            pb = GetButtonPositionP(row, col);
                            if (exc.Contains(rulename))
                            {
                                UI.Button(ref container, RPVERULESELECT, UI.Color("#55d840", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rulename} delete");
                            }
                            else
                            {
                                string ruleColor = "#5555cc";
                                if (custom) ruleColor = "#d85540";
                                UI.Button(ref container, RPVERULESELECT, UI.Color(ruleColor, 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {rulename} add");
                            }
                            row++;
                        }
                    }
                }
            }

            CuiHelper.AddUi(player, container);
        }

        private void GUISelectExclusion(BasePlayer player, string rulesetname, string srctgt)
        {
            Puts($"GUISelectExclusion called for {rulesetname}");
            CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);
            string t = Lang("source"); if (srctgt == "tgt_exclude") t = Lang("target");

            CuiElementContainer container = UI.Container(RPVERULEEXCLUSIONS, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeexclusions");
            UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Lang("realpveexclusions") + " " + t, 24, "0.2 0.92", "0.7 1");
            UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Name + " " + Version.ToString(), 12, "0.9 0.01", "0.99 0.05");

            int col = 0;
            int row = 0;

            List<string> foundsrc = new List<string>();
            List<string> foundtgt = new List<string>();
            string src_exclude = "";
            string tgt_exclude = "";

            // Get ruleset src and tgt exclusions
            Puts($"SELECT src_exclude, tgt_exclude FROM rpve_rulesets WHERE name='{rulesetname}'");
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand rs = new SQLiteCommand($"SELECT src_exclude, tgt_exclude FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SQLiteDataReader rsd = rs.ExecuteReader())
                    {
                        while (rsd.Read())
                        {
                            string a = rsd.GetValue(0).ToString();
                            if (a != "")
                            {
                                Puts($"Adding {a} to src_exclude");
                                src_exclude += a;
                            }
                            string b = rsd.GetValue(1).ToString();
                            if (b != "")
                            {
                                Puts($"Adding {b} to tgt_exclude");
                                tgt_exclude += b;
                            }
                        }
                    }
                }
            }

            // Get organized entities list
            Dictionary<string, RealPVEEntities> rpveentities = new Dictionary<string, RealPVEEntities>();
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand ent = new SQLiteCommand("SELECT name,type,custom FROM rpve_entities ORDER BY name", c))
                {
                    using (SQLiteDataReader ntd = ent.ExecuteReader())
                    {
                        while (ntd.Read())
                        {
                            string nm = ntd.GetString(0);
                            string tp = ntd.GetString(1);
                            if (nm == "" || tp == "") continue;
                            bool cs = ntd.GetBoolean(2);
                            //Puts($"Adding {nm} {tp} to entities list");
                            if (nm != "" && tp != "")
                            {
                                if (!rpveentities.ContainsKey(nm))
                                {
                                    rpveentities[nm] = new RealPVEEntities() { types = new List<string> { tp }, custom = cs };
                                }
                                else
                                {
                                    rpveentities[nm].types.Add(tp);
                                }
                            }
                        }
                    }
                }
            }

            // Go through the ruleset looking for exceptions, which we convert into entity types
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand crs = new SQLiteCommand($"SELECT exception from rpve_rulesets WHERE name='{rulesetname}' ORDER BY exception", c))
                {
                    using (SQLiteDataReader crd = crs.ExecuteReader())
                    {
                        List<string> exc = new List<string>();
                        while (crd.Read())
                        {
                            string rulename = crd.GetValue(0).ToString();
                            if (rulename == "") continue;
                            string src = null;
                            string tgt = null;

                            if (rulename != null && rulename != "")
                            {
                                string[] st = rulename.Split('_');
                                src = st[0]; tgt = st[1];
                            }

                            float[] pb = GetButtonPositionP(row, col);
                            switch (srctgt)
                            {
                                case "src_exclude":
                                    if (src == null || !rpveentities.ContainsKey(src)) break;
                                    foreach (string type in rpveentities[src].types)
                                    {
                                        Puts($"Checking for '{type}'");
                                        if (type == "") continue;
                                        if (foundsrc.Contains(type)) continue;
                                        foundsrc.Add(type);
                                        if (row > 10)
                                        {
                                            row = 0;
                                            col++;
                                        }
                                        pb = GetButtonPositionP(row, col);
                                        string eColor = "#d85540";

                                        Puts($"  Creating button for {type}, src_exclude='{src_exclude}'");
                                        if (!src_exclude.Contains(type))
                                        {
                                            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} add");
                                        }
                                        else
                                        {
                                            eColor = "#55d840";
                                            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} src_exclude {type} delete");
                                        }
                                        row++;
                                    }
                                    break;
                                case "tgt_exclude":
                                    if (tgt == null || !rpveentities.ContainsKey(tgt)) break;
                                    foreach (var type in rpveentities[tgt].types)
                                    {
                                        Puts($"Checking for '{type}'");
                                        if (type == "") continue;
                                        if (foundtgt.Contains(type)) continue;
                                        foundtgt.Add(type);
                                        if (row > 10)
                                        {
                                            row = 0;
                                            col++;
                                        }
                                        pb = GetButtonPositionP(row, col);
                                        string eColor = "#d85540";

                                        Puts($"  Creating button for {type}, tgt_exclude='{tgt_exclude}'");
                                        if (!tgt_exclude.Contains(type))
                                        {
                                            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} add");
                                        }
                                        else
                                        {
                                            eColor = "#55d840";
                                            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} tgt_exclude {type} delete");
                                        }
                                        row++;
                                    }
                                    break;
                            }
                        }
                        row++;
                    }
                }
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

            string description = "";
            string source = "";
            string target = "";
            bool damage = false;
            bool custom = false;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand getrs = new SQLiteCommand($"SELECT DISTINCT FROM rpve_rules WHERE name='{rulename}'", c))
                {
                    using (SQLiteDataReader rd = getrs.ExecuteReader())
                    {
                        description = "";
                        source = "";
                        target = "";
                        while (rd.Read())
                        {
                            description = rd.GetString(1);
                            damage = rd.GetBoolean(2);
                            custom = rd.GetBoolean(3);
                            source = rd.GetString(4);
                            target = rd.GetString(5);
                        }
                    }
                }
            }

            row = 0; col = 1;
            pb = GetButtonPositionP(row, col);
            if (!custom)
            {
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), source, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), target, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} name");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} description");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} damage");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), source, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} source");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), target, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} target");
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
            string zone = null;

            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand rs = new SQLiteCommand("SELECT DISTINCT zone FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SQLiteDataReader rsd = rs.ExecuteReader())
                    {
                        while (rsd.Read())
                        {
                            zone = rsd.GetString(0);
                        }
                    }
                }
            }

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
                    if (zone == null && rulesetname != "default")
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }
                    else
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#222222", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }

                    row++;
                    pb = GetButtonPositionZ(row, col);
                    if (zone == "default")
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
                        if (zName == zone) zColor = "#55d840";
                        if (zone == "lookup" && rpvezonemaps.ContainsKey(rulesetname))
                        {
                            if (rpvezonemaps[rulesetname].map.Contains(zoneID)) zColor = "#55d840";
                            labelonly = true;
                        }
                        if (zoneID.ToString() == zone) zColor = "#55d840";

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

            string schedule = null;
            using (SQLiteConnection c = new SQLiteConnection(connStr))
            {
                c.Open();
                using (SQLiteCommand rs = new SQLiteCommand("SELECT DISTINCT schedule FROM rpve_rulesets WHERE name='{rulesetname}'", c))
                {
                    using (SQLiteDataReader rsd = rs.ExecuteReader())
                    {
                        while (rsd.Read())
                        {
                            schedule = rsd.GetString(0);
                        }
                    }
                }
            }

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
        // Default entity categories and types to consider
        private void LoadDefaultEntities()
        {
            Puts("Trying to recreate entities data...");
            SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS rpve_entities", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("CREATE TABLE rpve_entities (name varchar(32), type varchar(32), custom INTEGER(1) DEFAULT 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'NPCPlayerApex', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'NPCPlayerApex', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'BradleyAPC', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'HumanNPC', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'BaseNpc', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'HTNPlayer', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Murderer', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Scientist', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npc', 'Zombie', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'ResourceEntity', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'LootContainer', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'DroppedItemContainer', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('resource', 'BaseCorpse', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'TeslaCoil', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'BearTrap', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'FlameTurret', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'Landmine', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'GunTrap', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'ReactiveTarget', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'spikes.floor', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('trap', 'Landmine', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'BaseAnimalNPC', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Boar', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Bear', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Chicken', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Stag', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Wolf', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('animal', 'Horse', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('fire', 'BaseOven', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('fire', 'FireBall', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('player', 'BasePlayer', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('building', 'BuildingBlock', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('helicopter', 'BaseHeli', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('minicopter', 'Minicopter', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('scrapcopter', 'ScrapTransportHelicopter', 0)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_entities VALUES('npcturret', 'NPCAutoTurret', 0)", sqlConnection);
            ct.ExecuteNonQuery();
        }

        private void LoadDefaultRuleset(bool drop=true)
        {
            Puts("Trying to recreate ruleset data...");
            if (drop)
            {
                SQLiteCommand cd = new SQLiteCommand("DROP TABLE IF EXISTS rpve_rulesets", sqlConnection);
                cd.ExecuteNonQuery();
                cd = new SQLiteCommand("CREATE TABLE rpve_rulesets (name VARCHAR(255), damage INTEGER(1) DEFAULT 0, enabled INTEGER(1) DEFAULT 1, automated INTEGER(1) DEFAULT 0, zone VARCHAR(255), exception VARCHAR(255), src_exclude VARCHAR(255), tgt_exclude VARCHAR(255), schedule VARCHAR(255))", sqlConnection);
                cd.ExecuteNonQuery();
            }
            SQLiteCommand ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_animal', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'animal_animal', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_helicopter', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_minicopter', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_npc', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npc_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_building', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_resource', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_animal', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'npcturret_npc', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'player_scrapcopter', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'scrapcopter_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'helicopter_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'trap_trap', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_player', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rulesets VALUES('default', 0, 1, 0, 0, 'fire_resource', null, null, null)", sqlConnection);
            ct.ExecuteNonQuery();
        }

        // Default rules which can be applied
        private void LoadDefaultRules()
        {
            Puts("Trying to recreate rules data...");
            SQLiteCommand ct = new SQLiteCommand("DROP TABLE IF EXISTS rpve_rules", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("CREATE TABLE rpve_rules (name varchar(32),description varchar(255),damage INTEGER(1) DEFAULT 1,custom INTEGER(1) DEFAULT 0,source VARCHAR(32),target VARCHAR(32));", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npc_player', 'NPC can damage player', 1, 0, 'npc', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_npc', 'Player can damage NPC', 1, 0, 'player', 'npc')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_player', 'Player can damage Player', 1, 0, 'player', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_building', 'Player can damage Building', 1, 0, 'player', 'building')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_resource', 'Player can damage Resource', 1, 0, 'player', 'resource')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('players_traps', 'Player can damage Trap', 1, 0, 'player', 'trap')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('traps_players', 'Trap can damage Player', 1, 0, 'trap', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_animal', 'Player can damage Animal', 1, 0, 'player', 'animal')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('animal_player', 'Animal can damage Player', 1, 0, 'animal', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('animal_animal', 'Animal can damage Animal', 1, 0, 'animal', 'animal')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('helicopter_player', 'Helicopter can damage Player', 1, 0, 'helicopter', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('helicopter_building', 'Helicopter can damage Building', 1, 0, 'helicopter', 'building')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_minicopter', 'Player can damage Minicopter', 1, 0, 'player', 'minicopter')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('minicopter_player', 'Minicopter can damage Player', 1, 0, 'minicopter', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_scrapcopter', 'Player can damage Scrapcopter', 1, 0, 'player', 'scrapcopter')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('scrapcopter_player', 'Scrapcopter can damage Player', 1, 0, 'scrapcopter', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('player_helicopter', 'Player can damage Helicopter', 1, 0, 'player', 'helicopter')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('highwalls_player', 'Highwall can damage Player', 1, 0, 'highwall', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_player', 'NPCAutoTurret can damage Player', 1, 0, 'npcturret', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_animal', 'NPCAutoTurret can damage Animal', 1, 0, 'npcturret', 'animal')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('npcturret_npc', 'NPCAutoTurret can damage NPC', 1, 0, 'npcturret', 'npc')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('fire_player', 'Fire can damage Player', 1, 0, 'fire', 'player')", sqlConnection);
            ct.ExecuteNonQuery();
            ct = new SQLiteCommand("INSERT INTO rpve_rules VALUES('fire_resource', 'Fire can damage Resource', 1, 0, 'fire', 'resource')", sqlConnection);
            ct.ExecuteNonQuery();
        }
        #endregion
    }
}
