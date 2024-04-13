using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker.Plugins;

public class PluginLinux : Plugin
{
    const string LIBRARY_NAME = "libdl.so.2"; // [libdl.so] isn't found on my bash, need to solve this discrepancy

    [DllImport(LIBRARY_NAME)]
    public static extern IntPtr dlopen(string fileName, int flags);

    [DllImport(LIBRARY_NAME)]
    public static extern IntPtr dlsym(IntPtr handle, string symbol);

    [DllImport(LIBRARY_NAME)]
    public static extern int dlclose(IntPtr handle);

    [DllImport(LIBRARY_NAME)]
    public static extern string dlerror();

    nint _handle;

    public override string AbsolutePath { get; }
    public override void Free()
    {
        base.Free();
        
        if (_handle == nint.Zero)
            return;
            
        dlclose(_handle);
        _handle = nint.Zero;
    }
    protected override nint FunctionAddress(string name) => dlsym(_handle, name);
    
    public PluginLinux(string absolute_path)
    {
        AbsolutePath = absolute_path;

        var handle = dlopen(absolute_path, 2);
        if (handle == nint.Zero)
            throw new Exception($"Failed to load plugin: {absolute_path} (Code: {Marshal.GetLastWin32Error()})");

        _handle = handle;

        LoadFunctions();
    }
}
