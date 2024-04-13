using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker.Plugins;

/// <summary>
/// This plugin implementation uses the new .NET class 
/// for managing native libraries that work across platforms
/// </summary>
public class PluginAny : Plugin
{
    nint _handle;

    public override string AbsolutePath { get; }
    public override void Free()
    {
        base.Free();
        
        if (_handle == nint.Zero)
            return;

        NativeLibrary.Free(_handle);
        _handle = nint.Zero;
    }

    protected override nint FunctionAddress(string name) 
    {
        if (NativeLibrary.TryGetExport(_handle, name, out nint address))
            return address;

        return nint.Zero;
    }
        
    public PluginAny(string absolute_path)
    {
        AbsolutePath = absolute_path;

        if (!NativeLibrary.TryLoad(absolute_path, out nint handle))
            throw new Exception($"Failed to load plugin: {absolute_path}");

        _handle = handle;
        LoadFunctions();
    }
}
