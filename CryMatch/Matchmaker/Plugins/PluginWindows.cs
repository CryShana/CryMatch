using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker.Plugins;

public class PluginWindows : Plugin
{
    const string LIBRARY_NAME = "kernel32.dll";

    [DllImport(LIBRARY_NAME, CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr LoadLibrary(string name);

    [DllImport(LIBRARY_NAME, CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    [DllImport(LIBRARY_NAME, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeLibrary(IntPtr hModule);

    nint _handle;

    public override string AbsolutePath { get; }
    public override void Free()
    {
        base.Free();
        
        if (_handle == nint.Zero)
            return;

        FreeLibrary(_handle);
        _handle = nint.Zero;
    }

    protected override nint FunctionAddress(string name) => GetProcAddress(_handle, name);
        
    public PluginWindows(string absolute_path)
    {
        AbsolutePath = absolute_path;

        var handle = LoadLibrary(absolute_path);
        if (handle == nint.Zero)
            throw new Exception($"Failed to load plugin: {absolute_path} (Code: {Marshal.GetLastWin32Error()})");

        _handle = handle;

        LoadFunctions();
    }
}
