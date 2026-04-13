# LocalCollidables — Ideas & Design Notes

## Core Concept

Prevent the player from walking through locally-spawned objects (props, walls, zone geometry) that only exist on their screen. The server must never see any desync or teleportation — it only sees the player stop walking naturally, as if they chose to stop.

## Why RMI Hook (Not Position Write)

Position writing causes server desync because:
- The client sends movement packets to the server
- If we snap the player back, the server disagrees with client position → rubber-band lag, possible kick
- With RMI hook: we intercept the *input* before it becomes a movement command
- The player simply never moved — no packet was sent for that direction
- The server agrees: "player stopped at position X" — because the player did stop at position X

## Sliding Collision Response

A dead stop (zeroing all movement) feels terrible. Instead, compute a sliding response:
```
wallNormal = closest face normal of the barrier AABB
dot = Vector3.Dot(moveDir, wallNormal)
if (dot < 0):  // moving INTO the wall
    moveDir -= dot * wallNormal  // remove component pointing into wall
    // moveDir now slides along wall face
```
This lets the player slide along walls naturally, exactly like game collision.

## Barrier Volume Types

- **AABB (Axis-Aligned Bounding Box):** Fast, good for boxes/crates/walls. Most props.
- **Sphere:** Simple, good for pillars/trees/round objects.
- **OBB (Oriented Bounding Box):** For rotated objects. More complex. Phase 2.

## IPC Surface (for LocalWorld integration)

```csharp
// LocalWorld calls these when spawning/despawning props
"LocalCollidables.RegisterAABB"   -> Action<Vector3 min, Vector3 max, uint id>
"LocalCollidables.RegisterSphere" -> Action<Vector3 center, float radius, uint id>
"LocalCollidables.RemoveVolume"   -> Action<uint id>
"LocalCollidables.ClearAll"       -> Action
```

## vnavmesh Cooperation

vnavmesh already hooks RMIWalkDelegate. Two hooks on the same address is fine — Dalamud's hook system chains them. But we need to yield when vnavmesh is navigating:
- Check `vnavmesh.Path.IsRunning()` at top of our detour
- If true: call Original immediately, skip our logic
- This prevents barriers from blocking automated navigation (which already knows about real terrain)

## Phase 1 Scope (MVP)

- AABB volumes only
- IPC registration from LocalWorld
- RMI hook with sliding response
- vnavmesh yield
- Manual barrier placement via command (for testing without LocalWorld)

## Phase 2 Ideas

- Sphere and OBB volumes
- Visual debug overlay showing barrier wireframes (toggle via command)
- Collision sounds (play a soft bump SFX from game files via ActionManager)
- "Pushback" mode: instead of stopping, apply a short push away from barrier

## Known Risks

- Signature for RMIWalkDelegate changes each FFXIV patch → must update
- If vnavmesh is NOT installed and another plugin also hooks RMI, need to handle chain gracefully
- Very thin barriers (<0.1 units) can be tunneled through at high movement speed → enforce minimum barrier thickness
