using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

using CryMatchGrpc;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace CryMatch.Core;

/// <summary>
/// Contains helper functions that either extend existing objects' functionality
/// or simply offer common reusable code
/// </summary>
public static class Extensions
{
    public static bool ParseAsEndpoint(this string text, [NotNullWhen(true)] out IPEndPoint? endpoint)
    {
        endpoint = null;
        ReadOnlySpan<char> full_text = text;
        ReadOnlySpan<char> address_text = full_text;
        ReadOnlySpan<char> port_text = "0";

        var port_split = full_text.IndexOf(':');
        if (port_split != -1)
        {
            address_text = full_text[..port_split];
            port_text = full_text[(port_split + 1)..];
        }

        if (!int.TryParse(port_text, out var port) || port < 0 || port > ushort.MaxValue)
            return false;

        if (!IPAddress.TryParse(address_text, out var address))
        {
            // it is probably a hostname, try resolving it
            var entry = Dns.GetHostEntry(address_text.ToString());
            address = entry.AddressList.FirstOrDefault();

            if (address == null)
                return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    public static WorkMode ParseAsWorkMode(this string? mode)
    {
        if (mode == null) return WorkMode.Standalone;

        if (string.Equals(WorkMode.Matchmaker.ToString(), mode, StringComparison.OrdinalIgnoreCase))
        {
            return WorkMode.Matchmaker;
        }
        else if (string.Equals(WorkMode.Director.ToString(), mode, StringComparison.OrdinalIgnoreCase))
        {
            return WorkMode.Director;
        }
        else
        {
            return WorkMode.Standalone;
        }
    }

    public static X509Certificate2? ParseCertificate(string? cert_path, string? key_path)
    {
        if (string.IsNullOrEmpty(cert_path) || !File.Exists(cert_path)) return null;

        var key_file_exists = !string.IsNullOrEmpty(key_path) && File.Exists(key_path);
        if (key_file_exists)
        {
            // both certificate and key file were provided, assumed PEM format
            var cert = X509Certificate2.CreateFromPemFile(cert_path, key_path);

            // NOTE: private key loaded this way will be marked as Ephemeral (short-term usage)
            // on Windows and this is rejected by SslStream, so we need to fix that
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // we export as PFX and re-import to fix it
                cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
            }

            return cert;
        }
        else
        {
            // certificate file also contains the key, assumed PFX format
            return new X509Certificate2(cert_path, "", X509KeyStorageFlags.Exportable);
        }
    }

    public static int FindMin<T>(this List<T> list, Func<T, int> property)
    {
        if (list.Count == 0) return -1;

        var span = CollectionsMarshal.AsSpan(list);
        
        var idx = 0;
        var min = property(span[0]);

        for (int i = 1; i < span.Length; i++)
        {
            var val = property(span[i]);
            if (val < min)
            {
                min = val;
                idx = i;
            }
        }

        return idx;
    }

    #region Ticket extensions
    public static MatchmakingRequirements AddRequirements(this Ticket ticket)
    {
        var requirements = new MatchmakingRequirements();
        ticket.Requirements.Add(requirements);
        return requirements;
    }

    public static MatchmakingRequirements AddDiscreet(this MatchmakingRequirements reqs, int key, params float[] values)
    {
        var dv = new Requirement();

        dv.Key = key;
        dv.Ranged = false;
        dv.Values.AddRange(values);

        reqs.Any.Add(dv);
        return reqs;
    }

    public static MatchmakingRequirements AddRange(this MatchmakingRequirements reqs, int key, float from, float to)
    {
        var rng = new Requirement();

        rng.Key = key;
        rng.Ranged = true;
        rng.Values.Add(from);
        rng.Values.Add(to);

        reqs.Any.Add(rng);
        return reqs;
    }

    public static RepeatedField<float> AddStateValue(this Ticket ticket, int key)
    {
        var v = new FloatArray();

        var state = ticket.State;
        var count = state.Count;
        if (count > key) state[key] = v;
        else if (count == key) state.Add(v);
        else
        {
            // fill in missing values
            for (int i = count; i < key; i++) 
                state.Add(new FloatArray());

            state.Add(v);
        }

        return v.Values;
    }
    #endregion
}
