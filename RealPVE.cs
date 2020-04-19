using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

// TODO
// Add the actual schedule handling...
// Finish work on custom rule editor (src/target)
// Verify all rules work as expected
// Sanity checking for overlapping rule/zone combinations.  Schedule may have impact.

namespace Oxide.Plugins
{
    [Info("Real PVE", "RFC1920", "1.0.4")]
    [Description("Prevent damage to players and objects in a PVE environment")]
    class RealPVE : RustPlugin
    {
        #region vars
        Dictionary<string, RealPVEEntities> pveentities = new Dictionary<string, RealPVEEntities>();
        Dictionary<string, RealPVERule> pverules = new Dictionary<string, RealPVERule>();
        Dictionary<string, RealPVERule> custom = new Dictionary<string, RealPVERule>();
        Dictionary<string, RealPVERuleSet> pverulesets = new Dictionary<string, RealPVERuleSet>();

        private const string permRealPVEUse = "realpve.use";
        private const string permRealPVEAdmin = "realpve.admin";
        private const string permRealPVEGod = "realpve.god";
        private ConfigData configData;

        [PluginReference]
        Plugin ZoneManager, LiteZones, HumanNPC, Friends, Clans, RustIO;

        private string logfilename = "log";
        private bool dolog = false;
        private bool enabled = true;

        const string RPVERULEEDITULELIST = "realpve.rulelist";
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
        void Init()
        {
            LoadConfigVariables();
            AddCovalenceCommand("pveenable", "cmdRealPVEenable");
            AddCovalenceCommand("pvelog", "cmdRealPVElog");
            AddCovalenceCommand("pverule", "cmdRealPVEGUI");
            permission.RegisterPermission(permRealPVEUse, this);
            permission.RegisterPermission(permRealPVEAdmin, this);
            permission.RegisterPermission(permRealPVEGod, this);
            enabled = true;
        }

        void OnServerInitialized()
        {
            LoadData();
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["notauthorized"] = "You don't have permission to use this command.",
                ["realpverulesets"] = "RealPVE Rulesets",
                ["realpveruleset"] = "RealPVE Ruleset",
                ["realpverule"] = "RealPVE Rule",
                ["realpvevalue"] = "RealPVE Ruleset Value",
                ["realpveexclusions"] = "RealPVE Ruleset Exclusions",
                ["realpveschedule"] = "RealPVE Ruleset Schedule",
                ["scheduling"] = "Schedules should be in the format of 'day(s);starttime;endtime'.  Use * for all days.",
                ["currentschedule"] = "Currently scheduled for day: {0} from {1} until {2}",
                ["noschedule"] = "Not currently scheduled",
                ["defload"] = "RESET",
                ["default"] = "default",
                ["none"] = "none",
                ["close"] = "Close",
                ["save"] = "Save",
                ["edit"] = "Edit",
                ["clicktoedit"] = "^Click to Edit^",
                ["editname"] = "Edit Name",
                ["add"] = "Add",
                ["all"] = "All",
                ["true"] = "True",
                ["false"] = "False",
                ["editing"] = "Editing",
                ["exclude"] = "Exclude",
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
                ["defaultdamage"] = "Default Damage",
                ["damageexceptions"] = "Damage Exceptions",
                ["rule"] = "Rule",
                ["rules"] = "Rules",
                ["logging"] = "Logging set to {0}"
            }, this);
        }

        void Unload()
        {
            foreach(BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, RPVERULEEDITULELIST);
                CuiHelper.DestroyUi(player, RPVERULEEDIT);
                CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);
                CuiHelper.DestroyUi(player, RPVEEDITRULESET);
                CuiHelper.DestroyUi(player, RPVERULESELECT);
                CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);
            }
        }

        void LoadData()
        {
            pveentities = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVEEntities>>(this.Name + "/" + this.Name + "_entities");
            if(pveentities.Count == 0)
            {
                LoadDefaultEntities();
            }
            pverules = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERule>>(this.Name + "/" + this.Name + "_rules");
            if(pverules.Count == 0)
            {
                LoadDefaultRules();
            }

            // Merge and overwrite from this file if it exists.
            custom = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERule>>(this.Name + "/" + this.Name + "_custom");
            if(custom.Count > 0)
            {
                foreach(KeyValuePair<string, RealPVERule> rule in custom)
                {
                    pverules[rule.Key] = rule.Value;
                }
            }
            custom.Clear();

            pverulesets = Interface.GetMod().DataFileSystem.ReadObject<Dictionary<string, RealPVERuleSet>>(this.Name + "/" + this.Name + "_rulesets");
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
            if(!enabled) return null;
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
            if(!enabled) return null;
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
            if(!enabled) return null;
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
                if((hitInfo.Initiator as BasePlayer).IPlayer.HasPermission(permRealPVEGod))
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
                DoLog($"DAMAGE BLOCKED for {stype} attacking {ttype}");
            }
            return true;
        }
        #endregion

        #region inbound_hooks
        bool AddOrUpdateMapping(string key, string rulesetname)
        {
            if(string.IsNullOrEmpty(key)) return false;
            if(rulesetname == null) return false;

            pverulesets[rulesetname].zone = key;
            SaveData();

            return true;
        }

        bool RemoveMapping(string key)
        {
            if(string.IsNullOrEmpty(key)) return false;

            foreach(KeyValuePair<string, RealPVERuleSet> pveruleset in pverulesets)
            {
                if(pveruleset.Value.zone == key) pverulesets[pveruleset.Key].zone = null;
            }

            return false;
        }
        #endregion

        #region Main
        private bool EvaluateRulesets(BaseEntity source, BaseEntity target, out string stype, out string ttype)
        {
            stype = source.GetType().Name;
            ttype = target.GetType().Name;
            string zone = "default";
            bool hasBP = false;

            // Special case since HumanNPC contains a BasePlayer object
            if(stype == "BasePlayer" && HumanNPC && IsHumanNPC(source)) stype = "HumanNPC";
            if(ttype == "BasePlayer" && HumanNPC && IsHumanNPC(target)) ttype = "HumanNPC";

            //var turret = source.Weapon?.GetComponentInParent<AutoTurret>();

            if(stype == "BasePlayer" && ttype == "BuildingBlock")
            {
                if(PlayerOwnsItem(source as BasePlayer, target)) hasBP = true;
            }

            bool zmatch = false;
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
                            string zName = (string)ZoneManager?.Call("GetZoneName", z);
                            zone = zName;
                            DoLog($"Found zone {zone}", 1);
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

            foreach(KeyValuePair<string,RealPVERuleSet> pveruleset in pverulesets)
            {
                if(!pveruleset.Value.enabled) continue;

                if(zone != "default" && zone != pveruleset.Value.zone)
                {
//                    DoLog($"Skipping check due to zone {zone} mismatch");
                    continue;
                }
                zmatch = true;

                DoLog($"Checking for match in ruleset {pveruleset.Key} (zone {zone}) for {stype} attacking {ttype}, default: {pveruleset.Value.damage.ToString()}");

                bool rulematch = false;

                foreach(string rule in pveruleset.Value.except)
                {
//                    string exclusions = string.Join(",", pveruleset.Value.exclude);
//                    Puts($"  Evaluating rule {rule} with exclusions: {exclusions}");
                    rulematch = EvaluateRule(rule, stype, ttype, pveruleset.Value.exclude);
                    if(rulematch)
                    {
//                        DoLog($"Matched rule {pveruleset.Key}", 1); //Log volume FIXME
                        return true;
                    }
                    else if(ttype == "BuildingBlock" && hasBP)
                    {
                        DoLog($"Damage allowed based on HonorBuildingPrivilege for {stype} attacking {ttype}", 1);
                        return true;
                    }
                }
                if(zmatch) return pveruleset.Value.damage;
            }

//            DoLog($"NO RULESET MATCH!");
            return false;
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
//                DoLog($"Checking for source match, {stype} == {src}?", 1); //Log volume FIXME
                if(stype == src)
                {
                    DoLog($"Matched {src} in {rulename} with target {ttype}", 2);
//                    if(exclude.Contains(stype))
//                    {
//                        srcmatch = false; // ???
//                        DoLog($"Exclusion match for source {stype}", 3);
//                        break;
//                    }
                    smatch = stype;
                    srcmatch = true;
                    break;
                }
            }
            foreach(string tgt in pverules[rulename].target)
            {
//                DoLog($"Checking for target match, {ttype} == {tgt}?", 1); //Log volume FIXME
                if(ttype == tgt && srcmatch)
                {
                    DoLog($"Matched {tgt} in {rulename} with source {stype}", 2);
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
                if(rulename.Contains("npcturret")) return true; // Log volume FIXME
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
            if(!enabled) return;
            if(message.Contains("Turret")) return; // Log volume FIXME
            if(dolog) LogToFile(logfilename, "".PadLeft(indent, ' ') + message, this);
        }

        [Command("pveenable")]
        void cmdRealPVEenable(IPlayer player, string command, string[] args)
        {
            if(!player.HasPermission(permRealPVEAdmin)) { Message(player, "notauthorized"); return; }

            enabled = !enabled;
            if(args.Length > 0)
            {
                if(args[0] == "gui") GUIRuleSets(player.Object as BasePlayer);
            }
            else
            {
                Message(player, "enableset", enabled.ToString());
            }
        }

        [Command("pvelog")]
        void cmdRealPVElog(IPlayer player, string command, string[] args)
        {
            if(!player.HasPermission(permRealPVEAdmin)) { Message(player, "notauthorized"); return; }

            dolog = !dolog;
            Message(player, "logging", dolog.ToString());
        }

        [Command("pverule")]
        void cmdRealPVEGUI(IPlayer iplayer, string command, string[] args)
        {
            if(!iplayer.HasPermission(permRealPVEAdmin)) { Message(iplayer, "notauthorized"); return; }
            var player = iplayer.Object as BasePlayer;

            if(args.Length > 0)
            {
                string debug = string.Join(",", args); Puts($"{debug}");

                switch(args[0])
                {
                    case "editruleset":
                        //e.g.: pverule editruleset {rulesetname} damage 0
                        //      pverule editruleset {rulesetname} name {newname}
                        //      pverule editruleset {rulesetname} zone {zonename}
                        // This is where we actually make the edit.
                        if(args.Length > 3)
                        {
                            string rs = args[1];
                            string setting = args[2];
                            string newval  = args[3];
                            CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                            switch(setting)
                            {
                                case "defload":
                                    rs = "default";
                                    pverulesets.Remove(rs);
                                    LoadDefaultRuleset();
                                    break;
                                case "enable":
                                    pverulesets[rs].enabled = GetBoolValue(newval);
                                    break;
                                case "damage":
                                    pverulesets[rs].damage = GetBoolValue(newval);
                                    break;
                                case "name":
                                    string newrs = args[3];
                                    pverulesets.Add(newrs, pverulesets[rs]);
                                    pverulesets.Remove(rs);
                                    rs = newrs;
                                    break;
                                case "zone":
                                    if(args[3] == "delete") pverulesets[rs].zone = null;
                                    else pverulesets[rs].zone = newval;
                                    break;
                                case "schedule":
                                    pverulesets[rs].schedule = newval;
                                    break;
                                case "except":
                                    if(args.Length < 5) return;
                                    //pverule editruleset {rulesetname} except {pverule.Key} add
                                    //pverule editruleset noturret except npcturret_animal delete
                                    switch(args[4])
                                    {
                                        case "add":
                                            pverulesets[rs].except.Add(newval);
                                            break;
                                        case "delete":
                                            pverulesets[rs].except.Remove(newval);
                                            break;
                                    }
                                    if(pverulesets[rs].except.Count == 0) pverulesets[rs].exclude.Clear(); // No exclude with exceptions...
                                    break;
                                case "exclude":
                                    if(args.Length < 5) return;
                                    //pverule editruleset {rulesetname} exclude Horse add
                                    switch(args[4])
                                    {
                                        case "add":
                                            pverulesets[rs].exclude.Add(newval);
                                            break;
                                        case "delete":
                                            pverulesets[rs].exclude.Remove(newval);
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
                        else if(args.Length > 2)
                        {
                            switch(args[2])
                            {
                                case "delete":
                                    pverulesets.Remove(args[1]);
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
                                case "exclude":
                                    GUISelectExclusion(player, args[1]);
                                    break;

                            }
                        }
                        //pverule editruleset {rulesetname}
                        else if(args.Length > 1)
                        {
                            string newname = args[1];
                            if(newname == "add")
                            {
                                int id = 0;
                                newname = "new1";
                                while(pverulesets.ContainsKey(newname))
                                {
                                    id++;
                                    if(id > 4) break;
                                    newname = "new" + id.ToString();
                                }
                                pverulesets.Add(newname, new RealPVERuleSet()
                                {
                                    damage = false, zone = null, schedule = null,
                                    except = new List<string>() {},
                                    exclude = new List<string>() {}
                                });
                                SaveData();
                            }
                            GUIRuleSets(player);
                            GUIRulesetEditor(player, newname);
                        }
                        break;
//                    case "editrule":
//                        Puts($"editrule {args[1]}");
//                        GUIRulesetEditor(player, args[1]);
//                        break;
                    case "close":
                        CuiHelper.DestroyUi(player, RPVERULEEDITULELIST);
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
                    case "editrule":
                        string rn = args[1];
                        GUIRuleEditor(player, rn);
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

        #region GUI
        void GUIRuleSets(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, RPVERULEEDITULELIST);

            CuiElementContainer container = UI.Container(RPVERULEEDITULELIST, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEDITULELIST, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule close");
            if(enabled)
            {
                UI.Button(ref container, RPVERULEEDITULELIST, UI.Color("#22ff22", 1f), Lang("genabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
            }
            else
            {
                UI.Button(ref container, RPVERULEEDITULELIST, UI.Color("#ff2222", 1f), Lang("gdisabled"), 12, "0.8 0.95", "0.92 0.98", $"pveenable gui");
            }
            UI.Label(ref container, RPVERULEEDITULELIST, UI.Color("#ffffff", 1f), Lang("realpverulesets"), 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;
            float[] pb;
            foreach(KeyValuePair<string, RealPVERuleSet> ruleset in pverulesets)
            {
                if(row > 10)
                {
                    row = 0;
                    col++;
                }
                pb = GetButtonPositionP(row, col);

                UI.Button(ref container, RPVERULEEDITULELIST, UI.Color("#d85540", 1f), ruleset.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {ruleset.Key}");
                row++;
            }
            pb = GetButtonPositionP(row, col);
            UI.Button(ref container, RPVERULEEDITULELIST, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset add");

            CuiHelper.AddUi(player, container);
        }

        void GUIRulesetEditor(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVEEDITRULESET);

            CuiElementContainer container = UI.Container(RPVEEDITRULESET, UI.Color("3b3b3b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("realpveruleset") + ": " + rulesetname, 24, "0.2 0.92", "0.7 1");
            if(pverulesets[rulesetname].enabled)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#22ff22", 1f), Lang("enabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 0");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("disabled"), 12, "0.78 0.95", "0.84 0.98", $"pverule editruleset {rulesetname} enable 1");
            }

            if(rulesetname == "default")
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("defload"), 12,"0.85 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} defload YES");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#2222ff", 1f), Lang("editname"), 12, "0.7 0.95", "0.76 0.98", $"pverule editruleset {rulesetname} name");
                // pverule editruleset {rulesetname} defload
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#ff2222", 1f), Lang("delete"), 12, "0.86 0.95", "0.92 0.98", $"pverule editruleset {rulesetname} delete");
            }
            UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleset");

            string dicolor = "#333333";
            string encolor = "#ff3333";
            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("defaultdamage"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("damageexceptions"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("exclude"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("zone"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("schedule"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row++;
            col = 0;

            pb = GetButtonPositionP(row, col);
            if(rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if(pverulesets[rulesetname].damage)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(encolor, 1f), Lang("true"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 0");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color(dicolor, 1f), Lang("false"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} damage 1");
            }

            col++;
            bool noExceptions = true;
            foreach(string except in pverulesets[rulesetname].except)
            {
                noExceptions = false;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), except, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except");
                row++;
            }
            if(noExceptions)
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
            if(!noExceptions) // Cannot exclude from exceptions that do not exist
            {
                foreach(string exclude in pverulesets[rulesetname].exclude)
                {
                    noExclusions = false;
                    if(row > 10)
                    {
                        row = 0;
                        col++;
                    }
                    pb = GetButtonPositionP(row, col);
                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), exclude, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude");
                    row++;
                }
                if(noExclusions)
                {
                    pb = GetButtonPositionP(row, col);
                    UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude");
                }
                else
                {
                    pb = GetButtonPositionP(row, col);
                    UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                }
            }

            col++; row = 1;
            pb = GetButtonPositionP(row, col);
            if(rulesetname == "default")
            {
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else if(pverulesets[rulesetname].zone != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), pverulesets[rulesetname].zone, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone");
            }
            row++;
            pb = GetButtonPositionP(row, col);
            UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++; row = 1;
            pb = GetButtonPositionP(row, col);
            if(pverulesets[rulesetname].schedule != null)
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#d85540", 1f), pverulesets[rulesetname].schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVEEDITRULESET, UI.Color("#ffffff", 1f), Lang("clicktoedit"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Button(ref container, RPVEEDITRULESET, UI.Color("#55d840", 1f), Lang("add"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule");
            }

            CuiHelper.AddUi(player, container);
        }

        // Select rule to add to ruleset
        void GUISelectRule(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVERULESELECT);

            CuiElementContainer container = UI.Container(RPVERULESELECT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULESELECT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleselect");
            UI.Label(ref container, RPVERULESELECT, UI.Color("#ffffff", 1f), Lang("realpverule"), 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;

            float[] pb = GetButtonPositionP(row, col);
            foreach(KeyValuePair<string, RealPVERule> pverule in pverules)
            {
                if(row > 10)
                {
                    row = 0;
                    col++;
                }
                pb = GetButtonPositionP(row, col);
                if(pverulesets[rulesetname].except.Contains(pverule.Key))
                {
                    UI.Button(ref container, RPVERULESELECT, UI.Color("#22ff22", 1f), pverule.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {pverule.Key} delete");
                }
                else
                {
                    string ruleColor = "#5555cc";
                    if(pverule.Value.custom) ruleColor = "#d85540";
                    UI.Button(ref container, RPVERULESELECT, UI.Color(ruleColor, 1f), pverule.Key, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} except {pverule.Key} add");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        void GUISelectExclusion(BasePlayer player, string rulesetname)
        {
            // Need to run over entities based on a check of what is in the rule...
            CuiHelper.DestroyUi(player, RPVERULEEXCLUSIONS);

            CuiElementContainer container = UI.Container(RPVERULEEXCLUSIONS, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeexclusions");
            UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color("#ffffff", 1f), Lang("realpveexclusions"), 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;

            List<string> foundit = new List<string>();
            foreach(var rulename in pverulesets[rulesetname].except)
            {
                string[] st = rulename.Split('_');
                string src = st[0]; string tgt = st[1];

                float[] pb = GetButtonPositionP(row, col);
                if(!pveentities.ContainsKey(src)) continue;
                foreach(var type in pveentities[src].types)
                {
                    if(foundit.Contains(type)) continue;
                    foundit.Add(type);
                    if(row > 10)
                    {
                        row = 0;
                        col++;
                    }
                    pb = GetButtonPositionP(row, col);
                    string eColor = "#d85540";
                    if(pverulesets[rulesetname].exclude.Contains(type))
                    {
                        eColor = "#22ff22";
                        //UI.Label(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                        UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude {type} delete");
                    }
                    else
                    {
                        UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude {type} add");
                    }
                    row++;
                }
                if(!pveentities.ContainsKey(tgt)) continue;
                foreach(var type in pveentities[tgt].types)
                {
                    if(foundit.Contains(type)) continue;
                    foundit.Add(type);
                    if(row > 10)
                    {
                        row = 0;
                        col++;
                    }
                    pb = GetButtonPositionP(row, col);
                    string eColor = "#d85540";
                    if(pverulesets[rulesetname].exclude.Contains(type))
                    {
                        eColor = "#22ff22";
                        UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude {type} delete");
                    }
                    else
                    {
                        UI.Button(ref container, RPVERULEEXCLUSIONS, UI.Color(eColor, 1f), type, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} exclude {type} add");
                    }
                    row++;
                }
            }

            CuiHelper.AddUi(player, container);
        }

        void GUIRuleEditor(BasePlayer player, string rulename)
        {
            CuiHelper.DestroyUi(player, RPVERULEEDIT);

            CuiElementContainer container = UI.Container(RPVERULEEDIT, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            UI.Button(ref container, RPVERULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerule");
            UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), Lang("realpverule") + ": " + rulename, 24, "0.2 0.92", "0.7 1");

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
            if(!pverules[rulename].custom)
            {
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), pverules[rulename].description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), pverules[rulename].damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), string.Join(",", pverules[rulename].source), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Label(ref container, RPVERULEEDIT, UI.Color("#ffffff", 1f), string.Join(",", pverules[rulename].target), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            }
            else
            {
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), rulename, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} name");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), pverules[rulename].description, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} description");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), pverules[rulename].damage.ToString(), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} damage");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), string.Join(",", pverules[rulename].source), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} source");
                row++;
                pb = GetButtonPositionP(row, col);
                UI.Button(ref container, RPVERULEEDIT, UI.Color("#2b2b2b", 1f), string.Join(",", pverules[rulename].target), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editrule {rulename} target");
            }

            CuiHelper.AddUi(player, container);
        }

        void GUIEditValue(BasePlayer player, string rulesetname, string key = null)
        {
            CuiHelper.DestroyUi(player, RPVEVALUEEDIT);

            CuiElementContainer container = UI.Container(RPVEVALUEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
            UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closerulevalue");
            UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("realpvevalue") + ": " + rulesetname + " " + key, 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;
            float[] pb = GetButtonPositionP(row, col);

            switch(key)
            {
                case "name":
                    pb = GetButtonPositionP(row, col);
                    UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), Lang("editname") + ":", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    col++;
                    pb = GetButtonPositionP(row, col);
                    UI.Label(ref container, RPVEVALUEEDIT, UI.Color("#535353", 1f), rulesetname, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
                    UI.Input(ref container, RPVEVALUEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} name ");
                    break;
                case "zone":
                    string[] zoneIDs = (string[])ZoneManager?.Call("GetZoneIDs");
                    if(pverulesets[rulesetname].zone == null && rulesetname != "default")
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#22ff22", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }
                    else
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#222222", 1f), Lang("none"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone delete");
                    }

                    row++;
                    pb = GetButtonPositionP(row, col);
                    if(pverulesets[rulesetname].zone == "default")
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#22ff22", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone default");
                    }
                    else
                    {
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color("#222222", 1f), Lang("default"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone default");
                    }

                    row++;
                    foreach(string zoneID in zoneIDs)
                    {
                        string zName = (string)ZoneManager?.Call("GetZoneName", zoneID);
                        string zColor = "#222222";
                        if(zName == pverulesets[rulesetname].zone) zColor = "#22ff22";

                        pb = GetButtonPositionP(row, col);
                        UI.Button(ref container, RPVEVALUEEDIT, UI.Color(zColor, 1f), zName + "(" + zoneID.ToString() + ")", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} zone {zName}");
                        row++;
                    }
                    break;
                default:
                    CuiHelper.DestroyUi(player, RPVEVALUEEDIT);
                    return;
                    break;
            }

            CuiHelper.AddUi(player, container);
        }

        void GUIEditSchedule(BasePlayer player, string rulesetname)
        {
            CuiHelper.DestroyUi(player, RPVESCHEDULEEDIT);

            string schedule = pverulesets[rulesetname].schedule;

            CuiElementContainer container = UI.Container(RPVESCHEDULEEDIT, UI.Color("4b4b4b", 1f), "0.15 0.15", "0.85 0.85", true, "Overlay");
            UI.Button(ref container, RPVESCHEDULEEDIT, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"pverule closeruleschedule");
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("realpveschedule") + ": " + rulesetname, 24, "0.2 0.92", "0.7 1");

            string fmtschedule = null;
            if(schedule != null)
            {
                try
                {
                    string[] realschedule = schedule.Split(';');//.ToArray();
                    Puts($"Schedule: {realschedule[0]} {realschedule[1]} {realschedule[2]}");
                    // WTF?
                    int day = 0;
                    string dayName = Lang("all") + "(*)";
                    if(int.TryParse(realschedule[0], out day))
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

            float[] pb = GetButtonPositionP(row, col, 4f);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), Lang("scheduling"), 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            row++;
            pb = GetButtonPositionP(row, col, 3f);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), fmtschedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");

            col++;
            pb = GetButtonPositionP(row, 4);
            UI.Label(ref container, RPVESCHEDULEEDIT, UI.Color("#535353", 1f), schedule, 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}");
            UI.Input(ref container, RPVESCHEDULEEDIT, UI.Color("#ffffff", 1f), " ", 12, $"{pb[0]} {pb[1]}", $"{pb[0] + ((pb[2] - pb[0]) / 2)} {pb[3]}", $"pverule editruleset {rulesetname} schedule ");

            CuiHelper.AddUi(player, container);
        }

        private int RowNumber(int max, int count) => Mathf.FloorToInt(count / max);
        private float[] GetButtonPosition(int rowNumber, int columnNumber)
        {
            float offsetX = 0.05f + (0.096f * columnNumber);
            float offsetY = (0.80f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + 0.196f, offsetY + 0.03f };
        }
        private float[] GetButtonPositionP(int rowNumber, int columnNumber, float colspan = 1f)
        {
            float offsetX = 0.05f + (0.126f * columnNumber);
            float offsetY = (0.87f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + (0.226f * colspan), offsetY + 0.03f };
        }
        #endregion

        #region Specialized_checks
        private string[] GetEntityZones(BaseEntity entity)
        {
            if(configData.Options.UseZoneManager)
            {
                if(entity is BasePlayer)
                {
                     return (string[]) ZoneManager?.Call("GetPlayerZoneIDs", new object[] { entity as BasePlayer });
                }
                else if(entity.IsValid())
                {
                     return (string[]) ZoneManager?.Call("GetEntityZoneIDs", new object[] { entity });
                }
            }
            else if(configData.Options.UseLiteZones)
            {
                if(entity.IsValid())
                {
                     return (string[]) LiteZones?.Call("GetEntityZoneIDs", new object[] { entity });
                }
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

        private bool PlayerOwnsItem(BasePlayer player, BaseEntity entity)
        {
            if(entity is BuildingBlock)
            {
                if(!configData.Options.HonorBuildingPrivilege) return true;

                BuildingManager.Building building = (entity as BuildingBlock).GetBuilding();

                if(building != null)
                {
                    var privs = building.GetDominatingBuildingPrivilege();
                    if(privs == null) return false;
                    foreach(var auth in privs.authorizedPlayers.Select(x => x.userid).ToArray())
                    {
                        if(entity.OwnerID == player.userID)
                        {
                            DoLog($"Player owns BuildingBlock", 2);
                            return true;
                        }
                        else if(player.userID == auth)
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
                if(configData.Options.HonorRelationships)
                {
                    if(IsFriend(player.userID, entity.OwnerID))
                    {
                        DoLog($"Player is friends with owner of entity", 2);
                        return true;
                    }
                }
                else if(entity.OwnerID == player.userID)
                {
                    DoLog($"Player owns BuildingBlock", 2);
                    return true;
                }
            }
            return false;
        }

        private static bool GetBoolValue(string value)
        {
            if(value == null) return false;
            value = value.Trim().ToLower();
            switch(value)
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
            if(configData.Options.useFriends && Friends != null)
            {
                var fr = Friends?.CallHook("AreFriends", playerid, ownerid);
                if(fr != null && (bool)fr)
                {
                    return true;
                }
            }
            if(configData.Options.useClans && Clans != null)
            {
                string playerclan = (string)Clans?.CallHook("GetClanOf", playerid);
                string ownerclan  = (string)Clans?.CallHook("GetClanOf", ownerid);
                if(playerclan == ownerclan && playerclan != null && ownerclan != null)
                {
                    return true;
                }
            }
            if(configData.Options.useTeams)
            {
                BasePlayer player = BasePlayer.FindByID(playerid);
                if(player.currentTeam != (long)0)
                {
                    RelationshipManager.PlayerTeam playerTeam = RelationshipManager.Instance.FindTeam(player.currentTeam);
                    if(playerTeam == null) return false;
                    if(playerTeam.members.Contains(ownerid))
                    {
                        return true;
                    }
                }
            }
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
            public bool HonorBuildingPrivilege = true;
            public bool HonorRelationships = false;
            public bool useFriends = false;
            public bool useClans = false;
            public bool useTeams = false;
        }
        #endregion

        #region classes
        public class RealPVERuleSet
        {
            public bool damage;
            public List<string> except;
            public List<string> exclude;
            public string zone;
            public string schedule;
            public bool enabled;
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
                if(hexColor.StartsWith("#"))
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
        private void LoadDefaultRuleset()
        {
            pverulesets.Add("default", new RealPVERuleSet()
            {
                damage = false, zone = null, schedule = null, enabled = true,
                except = new List<string>() { "animal_player", "player_animal", "animal_animal", "player_minicopter", "player_npc", "npc_player", "player_building", "player_resources", "npcturret_player", "npcturret_animal", "npcturret_npc" },
                exclude = new List<string>() {}
            });
        }

        // Default entity categories and types to consider
        private void LoadDefaultEntities()
        {
            pveentities.Add("npc", new RealPVEEntities() { types = new List<string>() { "NPCPlayerApex", "BradleyAPC", "HumanNPC", "BaseNpc", "HTNPlayer", "Murderer", "Scientist" } });
            pveentities.Add("player", new RealPVEEntities() { types = new List<string>() { "BasePlayer" } });
            pveentities.Add("building", new RealPVEEntities() { types = new List<string>() { "BuildingBlock" } });
            pveentities.Add("resource", new RealPVEEntities() { types = new List<string>() { "ResourceEntity", "LootContainer" } });
            pveentities.Add("trap", new RealPVEEntities() { types = new List<string>() { "TeslaCoil", "BearTrap", "FlameTurret", "Landmine", "GunTrap", "ReactiveTarget", "spikes.floor" } });
            pveentities.Add("animal", new RealPVEEntities() { types = new List<string>() { "BaseAnimalNPC", "Boar", "Bear", "Chicken", "Stag", "Wolf", "Horse" } });
            pveentities.Add("helicopter", new RealPVEEntities() { types = new List<string>() { "BaseHeli" } });
            pveentities.Add("minicopter", new RealPVEEntities() { types = new List<string>() { "Minicopter" } });
            pveentities.Add("highwall", new RealPVEEntities() { types = new List<string>() { "wall.external.high.stone", "wall.external.high.wood", "gates.external.high.wood", "gates.external.high.stone" } });
            pveentities.Add("npcturret", new RealPVEEntities() { types = new List<string>() { "NPCAutoTurret" } });
        }

        // Default rules which can be applied
        private void LoadDefaultRules()
        {
            pverules.Add("npc_player", new RealPVERule() { description = "npc can damage player", damage = true, custom = false, source = pveentities["npc"].types, target = pveentities["player"].types });
            pverules.Add("player_npc", new RealPVERule() { description = "Player can damage npc", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["npc"].types });
            pverules.Add("player_player", new RealPVERule() { description = "Player can damage player", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["player"].types });
            pverules.Add("player_building", new RealPVERule() { description = "Player can damage building", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["building"].types });
            pverules.Add("player_resources", new RealPVERule() { description = "Player can damage resource", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["resource"].types });
            pverules.Add("players_traps", new RealPVERule() { description = "Player can damage trap", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["trap"].types });
            pverules.Add("traps_players", new RealPVERule() { description = "Trap can damage player", damage = true, custom = false, source = pveentities["trap"].types, target = pveentities["player"].types });
            pverules.Add("player_animal", new RealPVERule() { description = "Player can damage animal", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["animal"].types });
            pverules.Add("animal_player", new RealPVERule() { description = "Animal can damage player", damage = true, custom = false, source = pveentities["animal"].types, target = pveentities["player"].types });
            pverules.Add("animal_animal", new RealPVERule() { description = "Animal can damage animal", damage = true, custom = false, source = pveentities["animal"].types, target = pveentities["animal"].types });
            pverules.Add("helicopter_player", new RealPVERule() { description = "Helicopter can damage player", damage = true, custom = false, source = pveentities["helicopter"].types, target = pveentities["player"].types });
            pverules.Add("helicopter_building", new RealPVERule() { description = "Helicopter can damage building", damage = true, custom = false, source = pveentities["helicopter"].types, target = pveentities["building"].types });
            pverules.Add("player_minicopter", new RealPVERule() { description = "Player can damage minicopter", damage = true, custom = false, source = pveentities["player"].types, target = pveentities["minicopter"].types });
            pverules.Add("minicopter_player", new RealPVERule() { description = "Minicopter can damage player", damage = true, custom = false, source = pveentities["minicopter"].types, target = pveentities["player"].types });
            pverules.Add("highwalls_player", new RealPVERule() { description = "Highwall can damage player", damage = true, custom = false, source = pveentities["highwall"].types, target = pveentities["player"].types });
            pverules.Add("npcturret_player", new RealPVERule() { description = "NPCAutoTurret can damage player", damage = true, custom = false, source = pveentities["npcturret"].types, target = pveentities["player"].types });
            pverules.Add("npcturret_animal", new RealPVERule() { description = "NPCAutoTurret can damage animal", damage = true, custom = false, source = pveentities["npcturret"].types, target = pveentities["animal"].types });
            pverules.Add("npcturret_npc", new RealPVERule() { description = "NPCAutoTurret can damage npc", damage = true, custom = false, source = pveentities["npcturret"].types, target = pveentities["npc"].types });
        }
        #endregion
    }
}
