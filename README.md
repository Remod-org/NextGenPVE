## NextGenPVE
Prevent damage to players and objects in a PVE environment

Uses ZoneManager, Friends, Clans, RustIO, GUIAnnouncements, HumanNPC (from remod.org), ZombieHorde

Works with DynamicPVP.

Don't let the documentation trouble you.  In most cases all you should need to do is install the plugin.  The rest is optional.

NEW FOR 1.2.8: There are 3 new configurations for scheduling a purge based on date and time.  Currently, this must be set in the config file.  Set purgeEnabled to true and useSchedule to true.  Set a start date (and time if desired) and end date (and time).  Note that you must update these values each month for them to be effective.  This can currently only happen once per month.

NEW FOR 1.0.52: Custom rule and entity collection editor - You must set AllowCustomEdit true in the configuration to enable this feature.  To go along with this, new entity types will be detected at wipe and should be available to set into existing or new collections for inclusion in rulesets.

### Overview
NextGenPVE is a new plugin and not a fork of TruePVE, et al.  It includes an integrated GUI for ruleset and entity management.

NextGenPVE is organized into entity collections, rules that use those collections, and rulesets that include a set of rules.

Each ruleset has a default damage value of true or false.

Each ruleset may include a list of rules which override the default setting called exceptions.

Each ruleset may include a list of exclusions to the exceptions that override those exceptions.

Each ruleset can and probably should be associated with a zone (if not the default ruleset).

Each ruleset can be either enabled or disabled.

The default ruleset (out of the box) has the following settings:

The builtin rules categorize entity types into collections, e.g. player, npc, fire, etc.

- Default damage false
- Exceptions:
    - animal can damage animal
    - animal can damage player
    - fire can damage building
    - fire can damage player
    - fire can damage resource
    - helicopter can damage building
    - helicopter can damage player
    - npc can damage player
    - npc turret can damage animal
    - npc turret can damage npc
    - npc turret can damage player
    - player can damage animal
    - player can damage building (their own or a friend's)
    - player can damage helicopter
    - player can damage minicopter
    - player can damage npc
    - player can damage resource
    - player can damage scrapcopter
    - resource can damage player
    - scrapcopter can damage player
    - trap can damage trap

- Exclusions: NONE (Could be chicken, bear, HumanNPC, etc.)

There is an integrated GUI for the admin to use to:

 1. Enable/disable NextGenPVE
 2. Create or delete rulesets
 3. Enable or disable rulesets
 4. Set the default damage for a ruleset
 5. Add rules for exceptions to the default damage setting of a ruleset
 6. Add exclusions for the rules
 7. Set the zone enabling activation of a ruleset
 8. Set a schedule for ruleset enable/disable (WORK IN PROGRESS)
 9. Edit custom rules (WORK IN PROGRESS)
10. Set global flags.
11. Create and edit custom rules.
12. Edit collections for ALL known entity types.

(Any new entity types should be added automatically on server wipe.  They will be in the unknown collection and by default will accept default ruleset damage.)

![](https://i.imgur.com/NbnonRb.jpg)

![](https://i.imgur.com/OgQ5nRt.jpg)

### Commands

The following commands have been implemented:

    - `/pveenable` - Toggles the enabled status of the plugin
    - `/pvelog` - Toggles the creation of a log file to monitor ruleset evaluation
    - `/pvedebug` - Toggles the logging of attacker/target and some other minor information to rcon and oxide logs.  This is saved in the config.
    - `/pverule` - Starts the GUI for editing, creating, and deleting rulesets

#### Sub-commands for /pverule

    - `/pverule list` - List current rulesets
  - `/pverule dump RULESETNAME` - List some information about a specific ruleset
  - `/pverule backup` - Same as /pvebackup.
  - `/pverule restore` - List available backup files from the plugin oxide data folder.
  - `/pverule restore FILENAME` - Restores the named database backup file to the live database.  The file must end in .db and MUST be from a previous backup created by NextGenPVE.  It must also be located in the plugin oxide data folder.

#### Additional sub-commands of /pverule called by GUI

    - `/pverule editconfig {CONFIG} true/false` - Set any of the global flags below to true/false
    - `/pverule editconfig RESET true` - Reset all of the global flags to default

    - `/pverule editruleset default defload YES` - Reset the default ruleset to defaults.
    - `/pverule editruleset {RULESETNAME} delete` - Delete the named ruleset.

    - `/pverule editruleset {RULESETNAME} name {NEWNAME}` - Change the name of a ruleset.
    - `/pverule editruleset {RULESETNAME} schedule {SCHEDULE}` - Set schedule for a ruleset.  Format is day;starthour:startminute;endhour:endminute, e.g. 1;1:00;9:00, 2;15:00;21:00.  Use '*' for every day
    - `/pverule editruleset {RULESETNAME} clone ` - Clone a ruleset.  The new name wil be {RULESETNAME}1 or {RULESETNAME}2, etc. if 1 exists.
    - `/pverule editruleset {RULESETNAME} damage 0/1` - Set default damage for the named ruleset.
    - `/pverule editruleset {RULESETNAME} enable 0/1` - Enable or disable the named ruleset.
    - `/pverule editruleset {RULESETNAME} except {RULENAME} add` - Add a named exception RULENAME to the named ruleset.
    - `/pverule editruleset {RULESETNAME} except {RULENAME} delete` - Delete a named exception RULENAME from the named ruleset.
    - `/pverule editruleset {RULESETNAME} src_exclude {TYPE} add` - Add a source exclusion, e.g. NPCMurderer.
    - `/pverule editruleset {RULESETNAME} src_exclude {TYPE} delete` - Delete a source exclusion, e.g. HumanNPC.
    - `/pverule editruleset {RULESETNAME} tgt_exclude {TYPE} add` - Add a target exclusion, e.g. NPCMurderer.
    - `/pverule editruleset {RULESETNAME} tgt_exclude {TYPE} delete` - Delete a target exclusion, e.g. HumanNPC.
    - `/pverule editruleset {RULESETNAME} zone default` - Set a ruleset zone to default.
    - `/pverule editruleset {RULESETNAME} zone delete` - Delete zone from a ruleset.
    - `/pverule editruleset {RULESETNAME} zone {zoneID}` - Set zone for ruleset.

    - `/pvedrop {gui} - Resets database to plugin defaults, removing any custom rules and entities.  Requires AllowDropDatabase config to be true.
  - `/pveupdate` - Update new entity types (normally run automatically at wipe, but can be run any time).  Any newly-detected entities will be added to the collection 'unknown'. 

The above commands can also be run from console or RCON (without /).

### Permissions

- nextgenpve.use   -- Currently unused
- nextgenpve.admin -- Required for access to GUI and other functions
- nextgenpve.god   -- Override PVE, killall

### Configuration

```json
{
  "Options": {
    "debug": false,
    "useZoneManager": false,
    "protectedDays": 0.0,
    "useSchedule": false,
    "purgeEnabled": false,
    "purgeStart": "12/31/1969 12:00",
    "purgeEnd": "1/1/1970 14:00",
    "useGUIAnnouncements": false,
    "useMessageBroadcast": false,
    "useRealTime": false,
    "useFriends": false,
    "useClans": false,
    "useTeams": false,
    "AllowCustomEdit": false,
    "AllowDropDatabase": false,
    "NPCAutoTurretTargetsPlayers": true,
    "NPCAutoTurretTargetsNPCs": true,
    "AutoTurretTargetsPlayers": false,
    "HeliTurretTargetsPlayers": true,
    "AutoTurretTargetsNPCs": false,
    "NPCSamSitesIgnorePlayers": false,
    "SamSitesIgnorePlayers": false,
    "AllowSuicide": false,
    "TrapsIgnorePlayers": false,
    "HonorBuildingPrivilege": true,
    "UnprotectedBuildingDamage": false,
    "UnprotectedDeployableDamage": false,
    "TwigDamage": false,
    "HonorRelationships": false,
    "BlockScrapHeliFallDamage": false,
    "requirePermissionForPlayerProtection": false,
    "enableDebugOnErrors": false,
    "autoDebugTime": 0.0
  },
  "Version": {
    "Major": 1,
    "Minor": 3,
    "Patch": 8
  }
}
```

ZoneManager can be used to associate a ruleset with a zone.

A few global flags are currently available to limit NPC AutoTurret and trap damage.

If a player is trying to damage a building, "HonorBuildingPrivilege" determines whether or not they are limited to damaging their own structures or any structures.

"UnprotectedDamage" determines whether or not an unprotected building (no TC) can be damaged by players other than the builder.

"HonorRelationships" determines whether or not a player can damage their friend's structures or deployables.

"BlockScrapHeliFallDamage" handles the special case where players flying the scrapheli into other players causes fall damage, killing the target player.

Note that friends support can include Friends, Clans, or Teams.

AllowCustomEdit - Enables the editing of custom rulesets and setting collections for entities.  Be careful here as you can easily categorize animals as NPCs, resources as players, etc.

AllowDropDatabase - Enables pvedrop command and GUI button to reset the database to defaults.  This wipes everything!!!

If protectedDays is set to any value other than zero, player buildings, etc. will only be protected if the user has been online sometime within that number of days.

"requirePermissionForPlayerProtection" - ONLY set this true if you want a default PVP setting with specific users or groups having protection.  In this case, perhaps the default rule damage would be set to true with no exceptions.  Or, you could make exception for player vs. deployables, etc.  Then, each player getting protection from player on player damage would need to have the nextgenpve.use permission.

"enableDebugOnErrors" - ONLY set this to automatically enable full debug for "autoDebugTime" seconds whenever we experience an NRE.  This requires the plugin NREHook, available at https://github.com/Remod-org/NREHook.  This is mostly useful only when diagnosing a problem with the developer.  As noted elsewhere, full debug (pvelog and pvedebug) can negatively impact performance.

### Details

NextGenPVE uses SQLite for most of its data storage.  The database file is named nextgenpve.db.

The only other data file is ngpve_zonemaps.json.  This is currently used by third party plugins that create their own PVP ruleset and zones.

Each rule includes a source and target listing all of the types that will be matched for the rule.  The player is simply BasePlayer, whereas NPCs include several different types.

Any individual type of NPC, for example, can be added to one of the "exclude" fields of a ruleset.  This can be source or target.  The list is based on the exception rules added to the ruleset, and the entity types they contain.

The default ruleset allows quite a bit of damage other than player to player.  For example, it has an exception for player_animal, allowing players to kill animals.  You can add, for example, "Chicken" to the target exclusion list to block killing just chickens.


The basic rule evaluation order is:

Ruleset -> Default Damage -> Exception Rule -> Exclusion.

Example 1:

1. Player attacking Bear
2. Default ruleset damage False.
3. Exception for player_animal.
4. No source exclusion for BasePlayer.
5. No target exclusion for Bear.
6. DAMAGE ALLOWED.

Example 2:

1. Bear attacking Player
2. Default ruleset damage False.
3. Exception for animal_player
4. No source exclusion for BasePlayer.
5. No target exclusion for Bear.
6. DAMAGE ALLOWED.

Example 3:

1. Player attacking Chicken
2. Default damage False.
3. Exception for player_animal.
4. No source exclusion for BasePlayer.
5. Target exclusion for Chicken.
6. DAMAGE BLOCKED.

### DynamicPVP

 For use with DynamicPVP, you may need to create a new ruleset.  Change the name to match the one that DynamicPVP uses - default name is "exclude".  Set that ruleset's default damage to true.  After that, reload DynamicPVP.  Your ruleset should look like this:
 spacer.png

 Note that the Zone is set to lookup.  You can click on "lookup" to see that the zone lookup for this is set to one or more DynamicPVP-created zones.  You should be able to adjust the rules for the zone to block things that would otherwise be allowed.


### Competing Ruleset Examples

    You create a clone of the default ruleset and enable it.
    You now have two rulesets with identical functionality including default damage, allow rules, and exclusions.
    Both rulesets would apply to the entire map by default.
    If you edit the allow rules or exclusions, the rulesets will compete.  The clone will likely override the default.
    Without a schedule or zone to determine which one is active at any given time or place, either may match for all PVE activity.
     FIX 1: Apply schedules to both rulesets
     FIX 2: Set a zone to the cloned ruleset (requires ZoneManager) to isolate it.

    You create a new ruleset with default damage TRUE and enable it
    You now have a ruleset which competes with the default ruleset.
    This new ruleset has default damage TRUE, which overrides the default ruleset.
    The entire map is now PVP.
     FIX 1: Add a zone to the new ruleset (requires ZoneManager) to isolate it to a specific area of the map.
     FIX 2: Add a schedule to the new ruleset.  A better option for scheduled PVP might be to add a schedule to the default ruleset and delete your secondary ruleset.

    In short, any rulesets you copy or create should be isolated by time and/or area using schedules or zones.  If your intention is to simply modify what types of damage is to be allowed globally, delete the extra rulesets and edit the default ruleset instead. 

### TODO
1. Performance tweaks as needed.
