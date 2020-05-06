# NextGenPVE - WORK IN PROGRESS
Prevent damage to players and objects in a PVE environment

Uses ZoneManager/LiteZones, Friends, Clans, RustIO, HumanNPC

## Overview
NextGenPVE is a new plugin and not a fork of TruePVE, et al.  It includes an integrated GUI for ruleset management.

NextGenPVE is organized into entity collections, rules that use those collections, and rulesets that include a set of rules.

Each ruleset has a default damage value of true or false.

Each ruleset may include a list of rules which override the default setting called exceptions.

Each ruleset may include a list of exclusions to the exceptions that override those exceptions.

Each ruleset can and probably should be associated with a zone (if not the default ruleset).

Each ruleset can be either enabled or disabled.

The default ruleset (out of the box) has the following settings:

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

![](https://i.imgur.com/dWiSvOB.jpg)

![](https://i.imgur.com/a6O9Aaf.jpg)

## Commands

The following commands have been implemented:

    - `/pverule` - Starts the GUI for editing, creating, and deleting rulesets
    - `/pveenable` - Toggles the enabled status of the plugin
    - `/pvelog` - Toggles the creation of a log file to monitor ruleset evaluation

The above commands can also be run from console or RCON.

## Permissions

- nextgenpve.use   -- Currently unused
- nextgenpve.admin -- Required for access to GUI and other functions
- nextgenpve.god   -- Override PVE, killall

## Configuration

```json
{
  "Options": {
    "useZoneManager": true,
    "useLiteZones": false,
    "useSchedule": false,
    "useRealtime": true,
    "useFriends": false,
    "useClans": false,
    "useTeams": false,
    "NPCAutoTurretTargetsPlayers": true,
    "NPCAutoTurretTargetsNPCs": true,
    "AutoTurretTargetsPlayers": false,
    "AutoTurretTargetsNPCs": false,
    "TrapsIgnorePlayers": false,
    "HonorBuildingPrivilege": true,
    "UnprotectedBuildingDamage": false,
    "HonorRelationships": false
  },
  "Version": {
    "Major": 1,
    "Minor": 0,
    "Patch": 16
  }
}
```

ZoneManager or LiteZones can be used to associate a ruleset with a zone.

A few global flags are currently available to limit NPC AutoTurret and trap damage.

If a player is trying to damage a building, "HonorBuildingPrivilege" determines whether or not they are limited to damaging their own structures or any structures.

"UnprotectedDamage" determines whether or not an unprotected building (no TC) can be damaged by players other than the builder.

"HonorRelationships" determines whether or not a player can damage their friend's structures or deployables.

Note that friends support can include Friends, Clans, or Teams.

## Details

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

