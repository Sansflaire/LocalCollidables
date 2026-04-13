# LocalCollidables — Research Document

## Core Directive

This document must be **fully filled out before implementation begins**. Every term below represents a concept used in this plugin's pipeline. Each section must contain: what the concept is, how it works, the exact API/signature used in Dalamud/FFXIV context, known gotchas, and any links or source references.

**Rules:**
- A section marked `[ EMPTY ]` is a blocker — do not implement the system that depends on it until research is complete
- If new terms emerge during development, add them here immediately
- Cross-reference with `GET_STARTED.md` to understand how each concept is used in context
- Update sections as confirmed working behavior is discovered (replace theory with fact)

---

## Terms Index

1. RMIWalkDelegate
2. IGameInteropProvider / HookFromSignature
3. Dalamud Hook\<T\>
4. IPC — Dalamud Inter-Plugin Communication
5. vnavmesh IPC (Path.IsRunning)
6. FFXIVClientStructs
7. IFramework.RunOnFrameworkThread
8. ObjectTable
9. IClientState.TerritoryChanged
10. AABB (Axis-Aligned Bounding Box)
11. Sliding Collision Response (Vector Projection)
12. Wall Normal Calculation

---

## 1. RMIWalkDelegate

**What it is:**
The game function responsible for converting raw input into the movement direction vector that is passed to the character movement system. "RMI" stands for Raw Movement Input. This is the correct intercept point for modifying or suppressing player movement without touching position memory.

**Signature (from vnavmesh source):**
```csharp
private delegate void RMIWalkDelegate(
    void* self,
    float* sumLeft,
    float* sumForward,
    float* sumTurnLeft,
    byte* haveBackwardOrStrafe,
    byte* a6,
    byte bAdditiveUnk);
```
`sumLeft` and `sumForward` are the output movement direction components. Zeroing both stops movement. Projecting them (sliding) allows movement along a wall.

**Byte signature string:**
```
// TODO: Copy confirmed sig string from vnavmesh source after confirming it compiles/hooks
// Look in: installedPlugins/vnavmesh/ for the sig scanner pattern
```

**Hook location:**
The function is called every game frame during movement processing. Hook via `IGameInteropProvider.HookFromSignature<RMIWalkDelegate>`.

**Gotchas:**
- The hook fires even when the player is not pressing any movement keys (all values will be 0.0f)
- vnavmesh also hooks this same delegate — if vnavmesh is navigating, its output takes priority. Check `vnavmesh.Path.IsRunning` before modifying
- Entire detour body must be in try/catch with `Original()` in finally — a crash here crashes the game

**Confirmed working:** `[ NOT YET CONFIRMED ]`

---

## 2. IGameInteropProvider / HookFromSignature

**What it is:**
Dalamud's service for creating function hooks in game memory. `HookFromSignature<TDelegate>` scans the game binary for a byte pattern and returns a hook object that intercepts calls to that function.

**API:**
```csharp
// Inject via constructor
private readonly IGameInteropProvider gameInterop;

// Create hook
hook = gameInterop.HookFromSignature<RMIWalkDelegate>("XX XX XX ...", DetourMethod);
hook.Enable();

// Detour method signature must match the delegate exactly
private void DetourMethod(void* self, float* sumLeft, ...) {
    try { /* your logic */ Original(self, sumLeft, ...); }
    catch (Exception e) { Log.Error(e, "detour"); }
    finally { /* ensure Original always called if needed */ }
}

// Cleanup
hook.Dispose(); // in Plugin.Dispose()
```

**Signature format:**
Space-separated hex bytes. `??` or `?` is a wildcard. Example: `"48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57"`

**Gotchas:**
- If the byte signature doesn't match the current game version, `HookFromSignature` will throw or hook the wrong address
- Always call `hook.Disable()` before `hook.Dispose()` in cleanup
- Hook is disabled by default after creation — must call `hook.Enable()`

**Confirmed working:** `[ See AutoReactFFXIV for reference implementation ]`

---

## 3. Dalamud Hook\<T\>

**What it is:**
The return type of `IGameInteropProvider.HookFromSignature<T>`. Wraps a detour using MinHook/EasyHook under the hood. Manages the trampoline (the "Original" call).

**API:**
```csharp
private Hook<RMIWalkDelegate>? rmiWalkHook;

// Access original function via:
rmiWalkHook!.Original(self, sumLeft, sumForward, ...);

// Lifecycle
rmiWalkHook.Enable();
rmiWalkHook.Disable();
rmiWalkHook.Dispose();
```

**Gotchas:**
- `Original` must be stored — calling the function directly after hooking will re-enter the detour (infinite loop)
- `Hook<T>` is IDisposable — dispose in plugin Dispose()
- Thread safety: the hook fires on the game thread; do not do expensive work inside it

**Confirmed working:** `[ See AutoReactFFXIV for reference ]`

---

## 4. IPC — Dalamud Inter-Plugin Communication

**What it is:**
Dalamud's pub/sub and RPC system for communication between loaded plugins. Uses named channels. A plugin can publish (fire-and-forget) or expose callable functions. Another plugin subscribes or invokes.

**Publisher (LocalWorld side — for reference):**
```csharp
var publisher = pluginInterface.GetIpcPublisher<uint, Vector3, Vector3>("LocalWorld.ActorSpawned");
publisher.SendMessage(actorId, center, extents);
```

**Subscriber (LocalCollidables side):**
```csharp
var sub = pluginInterface.GetIpcSubscriber<uint, Vector3, Vector3>("LocalWorld.ActorSpawned");
sub.Subscribe((id, center, extents) => {
    Framework.RunOnFrameworkThread(() => barrierManager.Add(...));
});
// Store sub — it must stay alive or the subscription is lost
// Unsubscribe in Dispose()
sub.Unsubscribe(handler);
```

**Callable IPC (return value):**
```csharp
// Publisher side
var provider = pluginInterface.GetIpcProvider<bool>("LocalCollidables.IsEnabled");
provider.RegisterFunc(() => config.Enabled);

// Subscriber side (caller)
var isEnabled = pluginInterface
    .GetIpcSubscriber<bool>("LocalCollidables.IsEnabled")
    .InvokeFunc();
```

**Gotchas:**
- Always wrap IPC calls in try/catch — the target plugin may not be loaded
- Subscriber objects must be kept alive (stored in a field) — if GC'd, subscription is silently lost
- Fire-and-forget messages can be missed if subscriber subscribes after message is sent
- Type parameters must match exactly between publisher and subscriber

**Confirmed working:** `[ Established pattern in this codebase ]`

---

## 5. vnavmesh IPC (Path.IsRunning)

**What it is:**
vnavmesh exposes its navigation state via Dalamud IPC. `vnavmesh.Path.IsRunning` returns true when vnavmesh is actively navigating the player to a destination. LocalCollidables must yield to vnavmesh when this is true.

**API:**
```csharp
private ICallGateSubscriber<bool>? vnavIsRunning;

// In Initialize:
vnavIsRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");

// In RMI detour:
try {
    if (vnavIsRunning?.InvokeFunc() == true) {
        Original(self, sumLeft, sumForward, ...);
        return;
    }
} catch { /* vnavmesh not loaded — ignore */ }
```

**Other useful vnavmesh IPC endpoints (for NPCDirector):**
```
vnavmesh.Nav.Pathfind(Vector3 from, Vector3 to, bool fly) -> List<Vector3>
vnavmesh.Nav.IsReady() -> bool
vnavmesh.Path.MoveTo(List<Vector3> waypoints, bool fly) -> void
vnavmesh.Path.Stop() -> void
```

**Gotchas:**
- vnavmesh may not be loaded — always guard with try/catch
- `IsRunning` returns true for the entire duration of navigation, not just when the player is moving
- The full list of IPC endpoints is in vnavmesh's source code (installedPlugins/vnavmesh/)

**Confirmed working:** `[ NOT YET CONFIRMED — verify endpoint name matches vnavmesh version ]`

---

## 6. FFXIVClientStructs

**What it is:**
A community-maintained C# library providing struct definitions and function signatures for FFXIV's internal game objects. Essential for accessing character data, ObjectTable entries, equipment, animations, and more.

**Key types used by LocalCollidables:**
```csharp
using FFXIVClientStructs.FFXIV.Client.Game.Object; // GameObject, ObjectTable
using FFXIVClientStructs.FFXIV.Client.Game.Character; // Character, DrawData

// Access local player character:
unsafe {
    var player = (Character*)clientState.LocalPlayer!.Address;
    var pos = player->GameObject.Position; // Vector3
}

// Access ObjectTable:
unsafe {
    var objTable = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectTable.Instance();
    for (int i = 0; i < 596; i++) {
        var obj = objTable->GetObjectByIndex(i);
        if (obj == null) continue;
        // ...
    }
}
```

**NuGet / reference:**
Included as part of Dalamud SDK. Do not add a separate NuGet package — reference via the Dalamud SDK.

**Gotchas:**
- All FFXIVClientStructs access must be inside `unsafe` blocks
- Struct layouts change with game patches — always verify against the current version
- Pointers can be null — always null-check before dereferencing
- Do not cache raw pointers across frames — the game may move memory

**Confirmed working:** `[ Established pattern — see JiggleLab, AutoReactFFXIV ]`

---

## 7. IFramework.RunOnFrameworkThread

**What it is:**
Dalamud's mechanism for marshaling work onto the game's main framework thread. Required for any memory write to game state (character data, ObjectTable, etc.) and any modification to data that is read by game-thread code.

**API:**
```csharp
private readonly IFramework framework;

// Marshal a write onto the framework thread:
framework.RunOnFrameworkThread(() => {
    barrierManager.Add(new BarrierVolume { ... });
});

// Also available: await-able version
await framework.RunOnFrameworkThread(() => { ... });
```

**When required:**
- Writing to any game struct (position, equipment, draw data, etc.)
- Modifying any List/Dictionary that is read from the game thread (hook detours)
- Any Dalamud API call that is documented as "main thread only"

**When NOT required:**
- Reading from game memory (reads are generally safe from any thread, though staleness is possible)
- Pure C# state (plugin config, non-game data)
- IPC message handlers that only queue work (then the actual write uses RunOnFrameworkThread)

**Gotchas:**
- Work queued via RunOnFrameworkThread executes on the NEXT framework tick, not immediately
- Do not await inside a game hook detour — hooks are synchronous
- Avoid heavy work inside framework thread callbacks — keep them short

**Confirmed working:** `[ Established pattern ]`

---

## 8. ObjectTable

**What it is:**
FFXIV's entity table — an array of up to 596 `GameObject` pointers representing all active entities in the current zone (players, NPCs, props, pets, etc.). Dalamud wraps this as `IObjectTable`.

**API (Dalamud wrapper):**
```csharp
private readonly IObjectTable objectTable;

foreach (var obj in objectTable)
{
    if (obj is PlayerCharacter pc) { ... }
    if (obj is BattleNpc bnpc) { ... }
    if (obj is EventObj eobj) { ... }
}

// Direct access:
var obj = objectTable.FirstOrDefault(o => o.EntityId == targetId);
```

**API (raw FFXIVClientStructs):**
```csharp
unsafe {
    var table = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectTable.Instance();
    var obj = table->GetObjectByIndex(i); // returns GameObject*
}
```

**Slot limits:**
- 0–199: player characters
- 200–245: event objects / interactive objects
- Remainder: battle NPCs, pets, minions, local spawned actors

**Gotchas:**
- Slots are reused — don't hold a slot index across frames; use EntityId for stable identity
- A spawned actor that leaks (not despawned) occupies a slot permanently until zone change
- The table has a hard cap — if all slots are full, new spawn calls will silently fail

**Confirmed working:** `[ Established pattern ]`

---

## 9. IClientState.TerritoryChanged

**What it is:**
Dalamud event that fires when the player changes zones (territories). Subscribe to this to reset any per-zone state — spawned actors, registered barriers, cached data.

**API:**
```csharp
private readonly IClientState clientState;

// In Plugin Initialize:
clientState.TerritoryChanged += OnTerritoryChanged;

// Handler:
private void OnTerritoryChanged(ushort territoryId)
{
    Framework.RunOnFrameworkThread(() => {
        barrierManager.Clear();
    });
}

// In Plugin Dispose:
clientState.TerritoryChanged -= OnTerritoryChanged;
```

**Gotchas:**
- Fires before the new zone is fully loaded — don't try to spawn actors immediately in the handler
- The ushort `territoryId` maps to `TerritoryType` Excel sheet row — use `DataManager.GetExcelSheet<TerritoryType>()?.GetRow(territoryId)` for zone metadata
- Also subscribe to `clientState.Logout` for the same cleanup logic

**Confirmed working:** `[ Established pattern ]`

---

## 10. AABB (Axis-Aligned Bounding Box)

**What it is:**
A rectangular 3D collision volume whose faces are always aligned to the world X/Y/Z axes. Defined by a minimum corner point (Min) and maximum corner point (Max). The simplest useful collision primitive — very fast intersection tests.

**Point-in-AABB test:**
```csharp
public static bool IsInside(Vector3 point, Vector3 min, Vector3 max)
{
    return point.X >= min.X && point.X <= max.X
        && point.Y >= min.Y && point.Y <= max.Y
        && point.Z >= min.Z && point.Z <= max.Z;
}
```

**Closest face normal (for sliding):**
```csharp
public static Vector3 GetClosestFaceNormal(Vector3 pointOutside, Vector3 min, Vector3 max)
{
    var center = (min + max) * 0.5f;
    var half = (max - min) * 0.5f;
    var local = pointOutside - center;

    // Find the face the point is closest to (from outside)
    float dx = half.X - Math.Abs(local.X);
    float dy = half.Y - Math.Abs(local.Y);
    float dz = half.Z - Math.Abs(local.Z);

    if (dx < dy && dx < dz) return new Vector3(Math.Sign(local.X), 0, 0);
    if (dz < dy)             return new Vector3(0, 0, Math.Sign(local.Z));
    return                          new Vector3(0, Math.Sign(local.Y), 0);
}
```

**Construction from center + extents:**
```csharp
var min = center - extents * 0.5f;
var max = center + extents * 0.5f;
```

**Why AABB and not OBB or sphere:**
- AABB is sufficient for most static props (rocks, barrels, walls) which are axis-aligned
- Near-zero compute cost per test
- Sphere would have gaps at corners; OBB requires expensive SAT tests and rotation math
- Can always upgrade to OBB later if needed for rotated props

**Gotchas:**
- AABB is in world space — if a prop is rotated, the AABB must be recomputed (it grows to fit the rotated shape), or we accept slightly oversized collision
- Y-axis collision matters for ramps — player can walk up/down without being blocked if AABB is too tall

---

## 11. Sliding Collision Response (Vector Projection)

**What it is:**
Instead of stopping movement entirely when a player would enter a collision volume, sliding projects the movement vector onto the wall plane so the player slides along it. This feels natural and is what real game engines do.

**Math:**
```csharp
// Given:
//   moveDir  = normalized movement direction
//   normal   = wall face normal (outward-facing, unit length)

// Remove the component of moveDir that points INTO the wall:
float penetration = Vector3.Dot(moveDir, normal);
if (penetration < 0) // moving toward the wall
{
    moveDir -= penetration * normal; // project onto wall plane
}

// moveDir now has the into-wall component removed
// Player slides along the wall instead of stopping
```

**Application in RMI detour:**
```csharp
var moveDir = new Vector3(*sumLeft, 0, *sumForward);
float moveMag = moveDir.Length();
if (moveMag > 0.001f)
{
    moveDir = Vector3.Normalize(moveDir);
    foreach (var vol in barriers.Volumes)
    {
        var nextPos = playerPos + moveDir * moveSpeed * dt;
        if (IsInside(nextPos, vol.Min, vol.Max))
        {
            var normal = GetClosestFaceNormal(playerPos, vol.Min, vol.Max);
            float dot = Vector3.Dot(moveDir, normal);
            if (dot < 0) moveDir -= dot * normal;
        }
    }
    moveDir *= moveMag; // restore original magnitude
}
*sumLeft    = moveDir.X;
*sumForward = moveDir.Z;
```

**Gotchas:**
- Normalize before projecting, then restore magnitude — otherwise speed changes near walls
- Multiple overlapping volumes may require multiple projection passes — iterate all volumes
- Corner cases: player in the corner of two walls. After first projection, re-check second wall. Two passes usually sufficient.

---

## 12. Wall Normal Calculation

**What it is:**
The outward-facing unit vector perpendicular to a wall face. Used in sliding collision to determine which direction to project movement. For an AABB, there are exactly 6 possible normals (±X, ±Y, ±Z).

**Which face is the player touching:**
The player's current position is used to determine which face of the AABB they are closest to (from the outside). The face they approach first determines the normal.

**Pre-computed AABB face normals:**
```csharp
static readonly Vector3[] AABBFaceNormals = {
    new Vector3( 1, 0, 0),  // +X (right face)
    new Vector3(-1, 0, 0),  // -X (left face)
    new Vector3( 0, 1, 0),  // +Y (top face)
    new Vector3( 0,-1, 0),  // -Y (bottom face)
    new Vector3( 0, 0, 1),  // +Z (forward face)
    new Vector3( 0, 0,-1),  // -Z (back face)
};
```

**In practice:**
Use the `GetClosestFaceNormal` function from section 10. This correctly identifies which face the player is about to enter from, and returns the outward normal for that face.

---

*Last updated: 2026-03-31 | Status: Initial research pass complete — implementation not yet started*
