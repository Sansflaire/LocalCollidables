using Dalamud.Configuration;

namespace LocalCollidables;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool ShowDebugOverlay { get; set; } = false;

    // TODO: serialized barrier volumes (for persistent manual placements)
}
