using System.Text;

namespace CryMatch.Core;

public class MatchmakerStatus
{
    public int ProcessingTickets { get; init; }
    public List<(string name, int in_queue, bool gathering)>? Pools { get; init; }
    public DateTime LocalTimeUtc { get; init; }

    const char _LINE_SEPARATOR_ = '\n';
    const char _DATA_SEPARATOR_ = '\t';

    public static string FromStatus(MatchmakerStatus status)
    {
        var builder = new StringBuilder(512);

        // total tickets
        builder.Append(
            $"{status.ProcessingTickets}{_DATA_SEPARATOR_}" +
            $"{status.LocalTimeUtc.ToBinary()}{_LINE_SEPARATOR_}");

        // pool status
        if (status.Pools != null)
            foreach (var pool in status.Pools)
                builder.Append(
                    $"{pool.name}{_DATA_SEPARATOR_}" +
                    $"{pool.in_queue}{_DATA_SEPARATOR_}" +
                    $"{(pool.gathering ? 1 : 0)}{_LINE_SEPARATOR_}");

        return builder.ToString();
    }

    public static MatchmakerStatus? ToStatus(ReadOnlySpan<char> text)
    {
        Span<Range> ranges = stackalloc Range[40];
        var ranges_count = text.Split(ranges, _LINE_SEPARATOR_, StringSplitOptions.RemoveEmptyEntries);
        if (ranges_count == 0) return null;

        var first_line = text[ranges[0]];
        var local_time_index = first_line.IndexOf(_DATA_SEPARATOR_);

        // total tickets
        if (!int.TryParse(first_line[..local_time_index], out var processing_tickets))
            return null;

        // local time
        if (!long.TryParse(first_line[(local_time_index + 1)..], out var local_time))
            return null;

        // pool statuses
        const int SEPARATOR_LENGTH = 1; // length of _DATA_SEPARATOR_ (for now can only be 1)

        var pools = new List<(string, int, bool)>(4);
        for (int i = 1; i < ranges_count; i++)
        {
            var line = text[ranges[i]];
            var length = line.Length;

            var inqueue_index = line.IndexOf(_DATA_SEPARATOR_) + SEPARATOR_LENGTH;
            if (inqueue_index == 0 || length <= inqueue_index) 
                continue;

            var gathering_index = line.Slice(inqueue_index).IndexOf(_DATA_SEPARATOR_) + inqueue_index + SEPARATOR_LENGTH;
            if (gathering_index == inqueue_index || length <= gathering_index) 
                continue;

            // parse
            var pool_name = line[..(inqueue_index - SEPARATOR_LENGTH)];
            if (!int.TryParse(line[inqueue_index..(gathering_index - SEPARATOR_LENGTH)], out var pool_queued))
                continue;

            var pool_gathering = line[gathering_index] == '1' ? true : false;

            pools.Add((
                pool_name.ToString(),
                pool_queued,
                pool_gathering
            ));
        }

        return new()
        {
            ProcessingTickets = processing_tickets,
            Pools = pools,
            LocalTimeUtc = DateTime.FromBinary(local_time)
        };
    }

    public override string ToString()
    {
        return FromStatus(this);
    }
}