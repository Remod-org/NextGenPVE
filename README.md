# RealPVE - WORK IN PROGRESS
Prevent damage to players and objects in a PVE environment

Uses ZoneManager/LiteZones, Friends, Clans, RustIO, HumanNPC

## Overview

RealPVE is organized into entity collections, rules that use those collections, and rulesets that include a set of rules.

Each ruleset has a default damage value of true or false.

Each ruleset may include a list of rules which override the default setting called exclusions.

Each ruleset may include a list of exclusions to the exceptions that override the exclusions.

Each ruleset can and probably should be associated with a zone (if not the default ruleset).

Each ruleset can be either enabled or disabled.

The default ruleset (out of the box) has the following settings:

- Default damage false
- Exceptions:
    - animal can damage player
	- player can damage animal
	- animal can damage animal
	- player can damage minicopter
	- player can damage npc
	- npc can damage player
	- player can damage building (their own or a friend's)
	- player can damage resources
	- npc turrets can damage players
	- npc turrets can damage animals
	- npc turrets can damage npcs
- Exclusions: NONE

There is in integrated GUI for the admin to use to:

 1. Enable/disable RealPVE
 2. Create or delete rulesets
 3. Enable or disable rulesets
 4. Set the default damage for a ruleset
 5. Add rules for exceptions to the default damage setting of a ruleset
 6. Add exclusions for the rules
 7. Set the zone enabling activation of a ruleset
 8. Set a schedule for ruleset enable/disable (WORK IN PROGRESS)
 9. Edit custom rules (WORK IN PROGRESS)

## Permissions

- realpve.use   -- Currently unused
- realpve.admin -- Required for access to GUI and other functions
- realpve.god   -- Override PVE, killall

## Configuration

```json
{
  "Options": {
    "UseZoneManager": true,
    "UseLiteZones": false,
    "NPCAutoTurretTargetsPlayers": true,
    "NPCAutoTurretTargetsNPCs": true,
    "AutoTurretTargetsPlayers": false,
    "AutoTurretTargetsNPCs": false,
    "TrapsIgnorePlayers": false,
    "HonorBuildingPrivilege": true,
    "HonorRelationships": false,
    "useFriends": false,
    "useClans": false,
    "useTeams": false
  }
}
```

ZoneManager or LiteZones can be used to associate a ruleset with a zone.

A few global flags are currently available to limit NPC AutoTurret and trap damage.

If a player is trying to damage a building, "HonorBuildingPrivilege" determines whether or not they are limited to damaging their own structures or any structures.

"HonorRelationships" determines whether or not a player can damage their friend's structures or deployables.

Note that friends support can include Friends, Clans, or Teams.

## Details and Data Files

The data file, RealPVE_rulesets.json, includes the following ruleset by default:

```json
{
  "default": {
    "damage": false,
    "except": [
      "animal_player",
      "player_animal",
      "animal_animal",
      "player_minicopter",
      "player_npc",
      "npc_player",
      "player_building",
      "player_resources",
      "npcturret_player",
      "npcturret_animal",
      "npcturret_npc"
    ],
    "exclude": [],
    "zone": null,
    "schedule": null,
    "enabled": true
  }
}
```

Following is a sample of the /RealPVE_rules.json data file.  The names of these rules match the "except" fields in the ruleset above:

```json
{
  "npc_player": {
    "description": "npc can damage player",
    "damage": true,
    "custom": false,
    "source": [
      "NPCPlayerApex",
      "BradleyAPC",
      "HumanNPC",
      "BaseNpc",
      "HTNPlayer",
      "Murderer",
      "Scientist"
    ],
    "target": [
      "BasePlayer"
    ]
  },
  "player_building": {
    "description": "Player can damage building",
    "damage": true,
    "custom": false,
    "source": [
      "BasePlayer"
    ],
    "target": [
      "BuildingBlock"
    ]
  }
}
```

Each rule includes a source and target listing all of the types that will be matched for the rule.  The player is simply BasePlayer, whereas NPCs include several different types.

Any individual type of NPC, for example, can be added to the "exclude" field of a ruleset.  Currently, this only excludes them as a potential target.

These types are populated from the RealPVE_entities.json file.  Here is a sample:

```json
{
  "npc": {
    "types": [
      "NPCPlayerApex",
      "BradleyAPC",
      "HumanNPC",
      "BaseNpc",
      "HTNPlayer",
      "Murderer",
      "Scientist"
    ]
  },
  "player": {
    "types": [
      "BasePlayer"
    ]
  },
  "building": {
    "types": [
      "BuildingBlock"
    ]
  }
}
```

