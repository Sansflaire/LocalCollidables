# LocalCollidables — Get Started

## What This Plugin Does

Prevents the player from walking through locally-spawned objects (props from LocalWorld) by intercepting movement input at the RMI hook level. The game server never sees any desync — from its perspective the player simply stopped moving, because the movement input was suppressed before it ever became a movement command.

## What's Already Done

- Full folder scaffold: `.sln`, `.csproj`, `.gitignore`, `README.md`, `CLAUDE.md`, `docs/IDEAS.md`
- `src/Plugin.cs` — stub with TerritoryChanged handler, command handler, UI draw, RMI hook placeholder
- `src/Configuration.cs` — `Enabled`, `ShowDebugOverlay` flags
- `pluginmaster.json` — ready for GitHub releases
- Full collision math design documented in `docs/IDEAS.md` (sliding response, AABB normals)

## What To Build First

**Prerequisite: LocalWorld must be working first.** LocalCollidables is pointless without objects to collide with. Start on LocalWorld, get actor spawning working, then come back here.

**Step 1 — Hook RMIWalkDelegate.**

The hook signature is in vnavmesh. Look at:
`C:\Users\trist\AppData\Roaming\XIVLauncher\installedPlugins\vnavmesh\`

Find the `RMIWalkDelegate` definition and signature string. Hook it via `IGameInteropProvider.HookFromSignature`:
```csharp
private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward,
    float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);

private Hook<RMIWalkDelegate>? rmiWalkHook;
```

Confirm the hook fires by logging `sumForward` and `sumLeft` values while walking.

**Step 2 — Implement BarrierManager.**

```csharp
public class BarrierVolume
{
    public uint Id;
    public Vector3 Min;   // AABB min corner
    public Vector3 Max;   // AABB max corner
}

public class BarrierManager
{
    private List<BarrierVolume> volumes = new();
    // Add / Remove / Clear methods
    // CheckAndSlide(Vector3 position, ref float sumLeft, ref float sumForward)
}
```

**Step 3 — Sliding collision in the detour.**

In the RMI detour, for each barrier volume:
```csharp
// Reconstruct movement direction from sumLeft/sumForward
var moveDir = new Vector3(*sumLeft, 0, *sumForward);

// For each volume: if next-frame position is inside, compute slide
var nextPos = playerPos + moveDir * moveSpeed * dt;
if (IsInsideAABB(nextPos, volume))
{
    var normal = GetClosestFaceNormal(playerPos, volume);
    var dot = Vector3.Dot(moveDir, normal);
    if (dot < 0) moveDir -= dot * normal; // slide along wall
}

*sumLeft = moveDir.X;
*sumForward = moveDir.Z;
```

**Step 4 — Subscribe to LocalWorld IPC.**

```csharp
var spawnedSub = PluginInterface.GetIpcSubscriber<uint, Vector3, Vector3>("LocalWorld.ActorSpawned");
spawnedSub.Subscribe((id, center, extents) => {
    Framework.RunOnFrameworkThread(() =>
        barrierManager.Add(new BarrierVolume { Id = id,
            Min = center - extents * 0.5f, Max = center + extents * 0.5f }));
});
```

**Step 5 — vnavmesh yield.**

At the top of the detour, check if vnavmesh is navigating and skip barrier logic if so:
```csharp
try {
    var isRunning = PluginInterface
        .GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning")
        .InvokeFunc();
    if (isRunning) { Original(...); return; }
} catch { /* vnavmesh not loaded */ }
```

## Key Technical Rules

- **NEVER write to player position** — only modify `sumLeft` / `sumForward` in the detour
- Entire detour body in try/catch; call `Original` in finally — DO NOT CRASH THE GAME
- All `BarrierVolume` list writes from IPC: `IFramework.RunOnFrameworkThread()`
- Yield to vnavmesh when it's navigating
- Barriers reset on `TerritoryChanged`

## Key Files To Read

- `CLAUDE.md` — collision math, vnavmesh cooperation, IPC surface, accent color
- `docs/IDEAS.md` — full sliding collision design, volume types, phase plan
- `src/Plugin.cs` — existing stubs
- **vnavmesh source** at `installedPlugins/vnavmesh/` — RMI hook delegate + signature

## Key References

- **vnavmesh** — RMI hook pattern, exact delegate signature, how they modify movement vectors
- **AutoReactFFXIV** `src/Plugin.cs` — reference for `IGameInteropProvider.HookFromSignature` pattern used in this codebase
- `devPlugins/CLAUDE.md` — cross-plugin rules, PanacheUI mandate

## Codebase Conventions

- Target: `net10.0-windows`, API Level 14, `x64`
- All Dalamud DLL refs: `Private="false"`
- UI: PanacheUI mandatory
- Author: `"Sansflaire"`
- CLAUDE.md is gitignored
