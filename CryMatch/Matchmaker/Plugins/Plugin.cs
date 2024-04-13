using CryMatchGrpc;

using System.Runtime.InteropServices;

namespace CryMatch.Matchmaker.Plugins;

public abstract class Plugin
{
    public abstract string AbsolutePath { get; }

    // NOTE: any pointers passed to or from plugin are owned by the plugin and should be freed by it

    #region Plugin functions
    public string Name()
    {
        try
        {
            var addr = _nativeName!.Invoke();
            return Marshal.PtrToStringAnsi(addr) ?? "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PluginLoader] Failed calling {Name} ('{path}')", nameof(Name), AbsolutePath);
            return "";
        }
    }

    public string HandledTicketPool()
    {
        try
        {
            var addr = _nativeHandledTicketPool!.Invoke();
            return Marshal.PtrToStringAnsi(addr) ?? "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PluginLoader] Failed calling {Name} ('{path}')", nameof(HandledTicketPool), AbsolutePath);
            return "";
        }
    }

    public int MatchSize(int tickets_in_pool)
    {
        try
        {
            return _nativeMatchSize!.Invoke(tickets_in_pool);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PluginLoader] Failed calling {Name} ('{path}')", nameof(MatchSize), AbsolutePath);
            return -1;
        }
    }

    public bool OverrideCandidatePicking()
    {
        try
        {
            return _nativeOverrideCandidatePicking!.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PluginLoader] Failed calling {Name} ('{path}')", nameof(MatchSize), AbsolutePath);
            return false;
        }
    }

    /// <summary>
    /// Given all available match candidates, pick candidates for match, put their indexes into [picked_candidates]
    /// </summary>
    /// <returns>True if match should be accepted, false if match should be rejected</returns>
    public bool PickMatchCandidates(Span<MatchCandidateNative> match_candidates, Span<int> picked_candidates)
    {
        try
        {
            unsafe
            {
                fixed (MatchCandidateNative* match_candidates_ptr = match_candidates)
                fixed (int* picked_candidates_ptr = picked_candidates)
                {
                    return _nativePickMatchCandidates!.Invoke(
                        match_candidates_ptr, match_candidates.Length, 
                        picked_candidates_ptr, picked_candidates.Length);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PluginLoader] Failed calling {Name} ('{path}')", nameof(MatchSize), AbsolutePath);
            return false;
        }
    }
    #endregion

    /// <summary>
    /// Frees resources held by plugin
    /// </summary>
    public virtual void Free()
    {
        try
        {
            _nativeFree?.Invoke();
        }
        catch { }
    }

    public static Plugin LoadFrom(string absolute_path)
    {
        if (!File.Exists(absolute_path))
            throw new FileNotFoundException($"Plugin does not exist: {absolute_path}");

        return new PluginAny(absolute_path);

        // Below is the old implementation for older
        // .NET versions left here as a comment for reference

        /*
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PluginWindows(absolute_path);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new PluginLinux(absolute_path);
        }
        else
        {
            throw new NotImplementedException("Platform not supported for plugins");
        }
        */
    }

    #region Loading logic
    protected abstract nint FunctionAddress(string name);
    protected void LoadFunctions()
    {
        try
        {
            _nativeFree = GetFunc<FreeDelegate>(nameof(Free));
            _nativeName = GetFunc<NameDelegate>(nameof(Name));
            _nativeMatchSize = GetFunc<MatchSizeDelegate>(nameof(MatchSize));
            _nativeHandledTicketPool = GetFunc<HandledTicketPoolDelegate>(nameof(HandledTicketPool));
            _nativePickMatchCandidates = GetFunc<PickMatchCandidatesDelegate>(nameof(PickMatchCandidates));
            _nativeOverrideCandidatePicking = GetFunc<OverrideCandidatePickingDelegate>(nameof(OverrideCandidatePicking));
        }
        catch
        {
            Free();
            throw;
        }
    }

    T GetFunc<T>(string name) where T : Delegate
    {
        var address = FunctionAddress(name);
        if (address == nint.Zero) throw new Exception($"Plugin is missing function '{name}': {AbsolutePath}");
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    protected FreeDelegate? _nativeFree;
    protected NameDelegate? _nativeName;
    protected MatchSizeDelegate? _nativeMatchSize;
    protected HandledTicketPoolDelegate? _nativeHandledTicketPool;
    protected PickMatchCandidatesDelegate? _nativePickMatchCandidates;
    protected OverrideCandidatePickingDelegate? _nativeOverrideCandidatePicking;

    protected delegate void FreeDelegate();
    protected delegate nint NameDelegate();
    protected delegate nint HandledTicketPoolDelegate();
    protected delegate int MatchSizeDelegate(int tickets_in_pool);
    protected delegate bool OverrideCandidatePickingDelegate();
    protected unsafe delegate bool PickMatchCandidatesDelegate(MatchCandidateNative* candidates, int candidates_size, int* picked_candidates, int picked_candidates_size);
    #endregion
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MatchCandidateNative : IDisposable
{
    public nint GlobalId = 0;
    public float CandidateRating;
    public float** State;
    public int* StateSizes;
    public int StateSize;

    public void SetDataTo(MatchCandidate candidate, ref GCHandle[] handles)
    {
        if (GlobalId != 0) Marshal.FreeHGlobal(GlobalId);   
        GlobalId = Marshal.StringToHGlobalAnsi(candidate.Ticket!.GlobalId);
        CandidateRating = candidate.Rating;

        var state = candidate.Ticket.State;
        for (int i = 0; i < state.Length; i++)
        {
            // we need to pin each float array to prevent GC from moving it in memory
            handles[i] = GCHandle.Alloc(state[i], GCHandleType.Pinned);
            State[i] = (float*)handles[i].AddrOfPinnedObject();
            StateSizes[i] = state[i].Length;
        }
    }

    public MatchCandidateNative(int state_size)
    {
        State = (float**)Marshal.AllocHGlobal(state_size * sizeof(float*));
        StateSizes = (int*)Marshal.AllocHGlobal(state_size * sizeof(int));
        StateSize = state_size;
    }

    public void Dispose()
    {
        if (GlobalId != 0) Marshal.FreeHGlobal(GlobalId);
        Marshal.FreeHGlobal((nint)StateSizes);
        Marshal.FreeHGlobal((nint)State);
    }
}