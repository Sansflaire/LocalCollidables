# LocalCollidables

A Dalamud plugin that adds invisible collision barriers around locally-spawned objects by intercepting player movement input at the RMI hook level.

## How It Works

Rather than writing to the player's position (which would cause server desync), LocalCollidables hooks the same `RMIWalkDelegate` used by vnavmesh. When the player attempts to walk into a registered barrier volume, the movement component pointing into the barrier is zeroed and a sliding collision response is computed — exactly like hitting a wall. The game server only ever sees the player stop applying movement input naturally.

## Commands

| Command | Description |
|---------|-------------|
| `/collidables` | Toggle the plugin window |
| `/collidables on` / `off` | Enable or disable all barriers |

## Dependencies

- **vnavmesh** (optional but recommended): If loaded, LocalCollidables yields to vnavmesh when automated pathfinding is active.
- **LocalWorld** (companion plugin): Automatically registers barrier volumes around props spawned by LocalWorld.

## Safety Rules

- Never writes to player position — only modifies the direction vector in the RMI detour.
- Entire detour body wrapped in try/catch; Original called in finally.
- Barriers are purely local — the game server has no awareness of them.

## Author

Sansflaire
