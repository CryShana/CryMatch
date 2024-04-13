using System.Collections.Frozen;

namespace CryMatch.Matchmaker.Plugins;

public class PluginLoader : IDisposable
{
    public string DirectoryPath { get; }

    FrozenDictionary<string, Plugin>? _plugins;
    public IReadOnlyCollection<Plugin>? Plugins => _plugins?.Values;

    public PluginLoader(string plugin_directory_relative)
    {
        var current_dir = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryPath = Path.Combine(current_dir, plugin_directory_relative);
    }

    /// <summary>
    /// Load all plugins from plugins directory
    /// </summary>
    /// <returns>Number of plugins loaded</returns>
    public int Load()
    {
        // free any old plugins
        if (_plugins != null)
        {
            foreach (var (_, plugin) in _plugins)
                plugin.Free();

            _plugins = null;
        }

        if (!Directory.Exists(DirectoryPath))
            return 0;

        // load new plugins
        var plugins = new Dictionary<string, Plugin>();
        var files = Directory.GetFiles(DirectoryPath);
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLower();
            if (string.IsNullOrEmpty(ext)) continue;

            if (ext is ".dll" or ".so")
            {
                try
                {
                    var plugin = Plugin.LoadFrom(f);
                    var name = plugin.Name();

                    if (string.IsNullOrEmpty(name))
                        throw new Exception("Plugin returned empty name");

                    if (plugins.ContainsKey(name))
                        throw new Exception("Plugin with same name already exists");

                    plugins[name] = plugin;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[PluginLoader] Failed to load plugin '{filename}'", Path.GetFileName(f));
                }
            }
        }

        _plugins = plugins.ToFrozenDictionary();

        return plugins.Count;
    }

    public Plugin? Get(string name) => _plugins?[name] ?? null;

    public string[] Loaded() => _plugins?.Keys.ToArray() ?? [];

    public void Dispose()
    {
        if (_plugins != null)
        {
            foreach (var (_, plugin) in _plugins)
                plugin.Free();

            _plugins = null;
        }
    }
}
