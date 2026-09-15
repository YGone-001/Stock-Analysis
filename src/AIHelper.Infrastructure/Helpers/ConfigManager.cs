using AIHelper.Models;
using Serilog;

namespace AIHelper.Helpers;

/// <summary>Compatibility façade for existing callers; persistence and secret policy live in ConfigStore.</summary>
public static class ConfigManager
{
    private static readonly ConfigStore Store = new(Path.Combine(AppContext.BaseDirectory, "config.json"));

    public static AppConfig Load() => Store.Load();

    public static void Save(AppConfig config)
    {
        if (!Store.TrySave(config, out string? error))
            Log.Warning("{Message}", error);
    }
}
