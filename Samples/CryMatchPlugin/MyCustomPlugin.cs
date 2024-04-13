using System.Runtime.InteropServices;

namespace CryMatchPlugin;

public static unsafe class MyCustomPlugin
{
    public static string PluginName => "MyCustomPlugin";
    public static string TicketPool => "";

    static nint _name;
    static nint _pool;

    static MyCustomPlugin()
    {
        _name = Marshal.StringToHGlobalAnsi(PluginName);
        _pool = Marshal.StringToHGlobalAnsi(TicketPool);
    }


    [UnmanagedCallersOnly(EntryPoint = "Name")]
    public static nint Name()
    {
        return _name;
    }

    [UnmanagedCallersOnly(EntryPoint = "HandledTicketPool")]
    public static nint HandledTicketPool()
    {
        return _pool;
    }

    [UnmanagedCallersOnly(EntryPoint = "MatchSize")]
    public static int MatchSize(int tickets_in_pool)
    {
        return 4;
    }

    [UnmanagedCallersOnly(EntryPoint = "OverrideCandidatePicking")]
    public static bool OverrideCandidatePicking()
    {
        return true;
    }

    [UnmanagedCallersOnly(EntryPoint = "PickMatchCandidates")]
    public static bool PickMatchCandidates(MatchCandidate* candidates, int candidates_size, int* picked_candidates, int picked_candidates_size)
    {
        //Console.WriteLine("[FromPlugin] PickMatchCandidates called");
        for (int i = 0; i < candidates_size; i++)
        {
            var candidate = candidates[i];
            var gid = Marshal.PtrToStringAnsi(candidate.GlobalId);

            //Console.WriteLine("[FromPlugin] Candidate Id        -> " + gid);
            //Console.WriteLine("[FromPlugin] Candidate rating    -> " + candidate.CandidateRating);

            for (int j = 0; j < candidate.StateSize; j++)
            {
                var state_values_size = candidate.StateSizes[j];
                var state_vaues = candidate.State[j];

                //Console.Write($"[FromPlugin] Candidate state [{j}]: ");
                for (int k = 0; k < state_values_size; k++)
                {
                    var val = state_vaues[k];
                    //Console.Write($"{val} ");
                }
                //Console.WriteLine();
            }

            //Console.WriteLine();
        }

        for (int i = 0; i < picked_candidates_size; i++)
        {
            //Console.WriteLine("[FromPlugin] Picked candidate    -> " + picked_candidates[i]);
        }
       
        return true;
    }

    [UnmanagedCallersOnly(EntryPoint = "Free")]
    public static void Free()
    {
        Marshal.FreeHGlobal(_name);
        Marshal.FreeHGlobal(_pool);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MatchCandidate
    {
        public nint GlobalId;
        public float CandidateRating;
        public float** State;
        public int* StateSizes;
        public int StateSize;
    }
}
