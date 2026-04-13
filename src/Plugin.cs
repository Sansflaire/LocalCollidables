using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace LocalCollidables;

/// <summary>
/// Hooks the game's RMIWalk (HandleMoveInput) function to apply AABB sliding collision
/// against barrier volumes registered from LocalWorld prop placements.
///
/// LocalWorld exposes LocalWorld.GetAllPropBounds() IPC callable → returns JSON of world-space AABBs.
/// LocalCollidables polls this every few seconds and on zone-in to populate its barrier list.
///
/// Hook approach: same as JumpSolver / vnavmesh. Call Original first, then modify sumLeft/sumForward
/// outputs to slide the player along barrier faces rather than stopping cold.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface     { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log                 { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager      { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework           { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState         { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable         { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInteropProvider { get; private set; } = null!;

    private const string CommandName = "/collidables";

    private readonly Configuration _config;

    // ── Barrier volumes (game-thread owned) ──────────────────────────────────

    private readonly record struct BarrierVolume(uint Id, Vector3 Min, Vector3 Max);
    private readonly List<BarrierVolume> _barriers = new();

    // ── RMI hook ──────────────────────────────────────────────────────────────

    // bool HandleMoveInput(void* self,
    //   float* sumLeft, float* sumForward, float* sumTurnLeft,
    //   byte* haveBackwardOrStrafe, byte* a6, byte a7)
    private unsafe delegate bool RMIWalkDelegate(
        void*  self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte*  haveBackwardOrStrafe,
        byte*  a6,
        byte   a7);

    // Accurate as of Dawntrail ~7.1x. Same sig used by JumpSolver and EasterEvent.
    private const string RMIWalkSig = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";

    private Hook<RMIWalkDelegate>? _rmiHook;

    // ── IPC ───────────────────────────────────────────────────────────────────

    private ICallGateSubscriber<bool>?   _vnavIsRunning;
    private ICallGateProvider<bool>?     _ipcIsEnabled;
    private ICallGateProvider<bool, object>? _ipcSetEnabled;

    // ── Sync state ────────────────────────────────────────────────────────────

    private long _lastSyncTicks;
    private const long SyncIntervalTicks = 5 * TimeSpan.TicksPerSecond; // poll every 5 s

    private bool _pendingZoneSync;
    private int  _zoneDelay;           // frame countdown after zone change

    // ─────────────────────────────────────────────────────────────────────────

    public Plugin()
    {
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Register IsEnabled / SetEnabled IPC so LocalWorld's UI can toggle us
        try
        {
            _ipcIsEnabled = PluginInterface.GetIpcProvider<bool>("LocalCollidables.IsEnabled");
            _ipcIsEnabled.RegisterFunc(() => _config.Enabled);

            _ipcSetEnabled = PluginInterface.GetIpcProvider<bool, object>("LocalCollidables.SetEnabled");
            _ipcSetEnabled.RegisterFunc(v =>
            {
                _config.Enabled = v;
                PluginInterface.SavePluginConfig(_config);
                Log.Info($"[LocalCollidables] Collisions {(v ? "enabled" : "disabled")} via IPC.");
                return null!;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LocalCollidables] IPC provider registration failed.");
        }

        // vnavmesh check — skip barrier collision while vnavmesh is navigating
        try { _vnavIsRunning = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning"); } catch { }

        // RMI hook — same sig as JumpSolver
        try
        {
            _rmiHook = GameInteropProvider.HookFromSignature<RMIWalkDelegate>(RMIWalkSig, RMIWalkDetour);
            _rmiHook.Enable();
            Log.Info("[LocalCollidables] RMI hook installed.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LocalCollidables] RMI hook failed — collision disabled. Update RMIWalkSig if game patched.");
        }

        // Slash command
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/collidables — Toggle collisions on/off",
        });

        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update += OnFrameworkUpdate;

        // Initial sync — pick up any props already placed (e.g. plugin loaded mid-session)
        SyncProps();

        Log.Info("[LocalCollidables] Loaded.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        CommandManager.RemoveHandler(CommandName);
        _rmiHook?.Dispose();
        Log.Info("[LocalCollidables] Unloaded.");
    }

    // ── RMI detour — sliding AABB collision ──────────────────────────────────

    private int _dbgLogCooldown;

    private unsafe bool RMIWalkDetour(
        void*  self,
        float* sumLeft,
        float* sumForward,
        float* sumTurnLeft,
        byte*  haveBackwardOrStrafe,
        byte*  a6,
        byte   a7)
    {
        try
        {
            bool result = _rmiHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, a7);

            if (!_config.Enabled)           return result;
            if (_barriers.Count == 0)        return result;
            if (*sumLeft == 0f && *sumForward == 0f) return result;

            try { if (_vnavIsRunning?.InvokeFunc() == true) return result; } catch { }

            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return result;

            var pos = localPlayer.Position;
            var rot = localPlayer.Rotation;

            // Convert character-local RMI outputs → world-space velocity vector.
            // forward (sumForward > 0) = (sin θ, 0, cos θ)
            // left    (sumLeft    > 0) = (−cos θ, 0, sin θ)
            float fX = MathF.Sin(rot), fZ = MathF.Cos(rot);
            float lX = -MathF.Cos(rot), lZ = MathF.Sin(rot);
            float wDX = *sumForward * fX + *sumLeft * lX;
            float wDZ = *sumForward * fZ + *sumLeft * lZ;
            if (wDX * wDX + wDZ * wDZ < 1e-6f) return result;

            const float PlayerRadius = 0.5f;
            const float PlayerHeight = 2.0f;

            bool modified = false;
            foreach (var vol in _barriers)
            {
                if (pos.Y >= vol.Max.Y - 0.5f)        continue;  // on top
                if (pos.Y + PlayerHeight <= vol.Min.Y) continue;  // below

                // Closest point on XZ AABB to player center
                float clampX = MathF.Max(vol.Min.X, MathF.Min(pos.X, vol.Max.X));
                float clampZ = MathF.Max(vol.Min.Z, MathF.Min(pos.Z, vol.Max.Z));
                float dX = pos.X - clampX;
                float dZ = pos.Z - clampZ;
                float distSq = dX * dX + dZ * dZ;
                if (distSq >= PlayerRadius * PlayerRadius) continue;

                if (--_dbgLogCooldown <= 0)
                {
                    _dbgLogCooldown = 120;
                    Log.Info($"[LC] HIT vol#{vol.Id} min=({vol.Min.X:F2},{vol.Min.Y:F2},{vol.Min.Z:F2}) " +
                             $"max=({vol.Max.X:F2},{vol.Max.Y:F2},{vol.Max.Z:F2}) " +
                             $"pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) dist={MathF.Sqrt(distSq):F3}");
                }

                float nx, nz;
                float dist = MathF.Sqrt(distSq);
                if (dist < 0.001f)
                {
                    // Inside AABB — normal toward nearest exit face
                    float cx  = (vol.Min.X + vol.Max.X) * 0.5f;
                    float cz  = (vol.Min.Z + vol.Max.Z) * 0.5f;
                    float lpx = pos.X - cx, lpz = pos.Z - cz;
                    float dpx = (vol.Max.X - vol.Min.X) * 0.5f - MathF.Abs(lpx);
                    float dpz = (vol.Max.Z - vol.Min.Z) * 0.5f - MathF.Abs(lpz);
                    nx = dpx < dpz ? (lpx >= 0f ? 1f : -1f) : 0f;
                    nz = dpx < dpz ? 0f : (lpz >= 0f ? 1f : -1f);
                }
                else
                {
                    // Outside AABB — normal from surface to player
                    nx = dX / dist;
                    nz = dZ / dist;
                }

                // Hard stop: only block movement going INTO the wall.
                // dot >= 0 (backing away or parallel) is always allowed.
                float dot = wDX * nx + wDZ * nz;
                if (dot < 0f)
                {
                    wDX = 0f;
                    wDZ = 0f;
                    modified = true;
                }
            }

            if (modified)
            {
                *sumForward = wDX * fX + wDZ * fZ;
                *sumLeft    = wDX * lX + wDZ * lZ;
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LocalCollidables] Exception in RMI detour.");
            return _rmiHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, a7);
        }
    }

    // ── Prop sync ─────────────────────────────────────────────────────────────

    private void SyncProps()
    {
        try
        {
            var json = PluginInterface
                .GetIpcSubscriber<string>("LocalWorld.GetAllPropBounds")
                .InvokeFunc();

            _barriers.Clear();

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                uint id = item.GetProperty("id").GetUInt32();
                var center = new Vector3(
                    item.GetProperty("cx").GetSingle(),
                    item.GetProperty("cy").GetSingle(),
                    item.GetProperty("cz").GetSingle());
                var half = new Vector3(
                    item.GetProperty("ex").GetSingle(),
                    item.GetProperty("ey").GetSingle(),
                    item.GetProperty("ez").GetSingle());
                _barriers.Add(new BarrierVolume(id, center - half, center + half));
            }

            if (_barriers.Count > 0)
                Log.Info($"[LocalCollidables] Synced {_barriers.Count} prop barrier(s).");
        }
        catch { /* LocalWorld not loaded, or no props — silently skip */ }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    private void OnTerritoryChanged(ushort territoryId)
    {
        _barriers.Clear();
        _pendingZoneSync = true;
        _zoneDelay = 180; // ~3 seconds at 60 fps — let LocalWorld finish loading the saved scene
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        // Delayed sync after zone change
        if (_pendingZoneSync)
        {
            if (--_zoneDelay <= 0)
            {
                _pendingZoneSync = false;
                SyncProps();
            }
            return;
        }

        // Heartbeat sync — picks up newly placed props within 5 s
        long now = DateTime.UtcNow.Ticks;
        if (now - _lastSyncTicks > SyncIntervalTicks)
        {
            _lastSyncTicks = now;
            SyncProps();
        }
    }

    // ── Command ───────────────────────────────────────────────────────────────

    private void OnCommand(string command, string args)
    {
        _config.Enabled = !_config.Enabled;
        PluginInterface.SavePluginConfig(_config);
        Log.Info($"[LocalCollidables] Collisions {(_config.Enabled ? "enabled" : "disabled")}.");
    }
}
