using Grpc.Core;

using CryMatchGrpc;
using CryMatch.Director;

namespace CryMatch.Services;

public class DirectorService : CryMatchGrpc.Director.DirectorBase
{
    readonly IState _state;
    readonly DirectorManager _director;

    public DirectorService(DirectorManager director, IState state)
    {
        _state = state;
        _director = director;
    }

    public override Task<DirectorResponse> TicketSubmit(Ticket request, ServerCallContext context)
    {
        Log.Debug("[Director] Ticket submit request [{address}]", context.Peer);

        var status = _director.TicketSubmit(request);

        Log.Debug("[Director] Ticket submit response: {id} ({sid}) -> {status} [{address}]", request.GlobalId, request.StateId, status, context.Peer);

        return Task.FromResult(new DirectorResponse
        {
            Status = status
        });
    }
     
    public override async Task<DirectorResponse> TicketRemove(Ticket request, ServerCallContext context)
    {
        Log.Debug("[Director] Ticket removal request: {id} (State: {sid}) [{address}]", request.GlobalId, context.Peer);

        var status = await _director.TicketRemove(request.GlobalId);

        Log.Debug("[Director] Ticket removal response: {id} -> {status} [{address}]", request.GlobalId, status, context.Peer);

        return new DirectorResponse
        {
            Status = status
        };
    }

    public override async Task GetTicketMatches(Empty request, IServerStreamWriter<TicketMatch> responseStream, ServerCallContext context)
    {
        Log.Debug("[Director] Reader connected for ticket matches [{address}]", context.Peer);

        await _director.ReadIncomingMatches((match) => responseStream.WriteAsync(match));

        Log.Debug("[Director] Reader disconnected for ticket matches [{address}]", context.Peer);    
    }

    public override async Task<PoolConfiguration> GetPoolConfiguration(PoolId request, ServerCallContext context)
    {
        Log.Debug("[Director] Client requested pool configuration for {pool_id} [{address}]", request.Id, context.Peer);

        var pid = request.Id;

        var match_size = await _state.GetString(IState.POOL_MATCH_SIZE_PREFIX + pid);
        if (string.IsNullOrEmpty(match_size) || !int.TryParse(match_size, out var parsed_match_size)) 
            return new PoolConfiguration
            {
                PoolId = pid,
                MatchSize = 2
            };

        return new PoolConfiguration
        {
            PoolId = pid,
            MatchSize = parsed_match_size
        };
    }

    public override async Task<DirectorResponse> SetPoolConfiguration(PoolConfiguration request, ServerCallContext context)
    {
        Log.Debug("[Director] Client provided new pool configuration for {pool_id} [{address}]", request.PoolId,  context.Peer);

        var pid = request.PoolId;

        await _state.SetString(IState.POOL_MATCH_SIZE_PREFIX + pid, request.MatchSize.ToString());

        return new DirectorResponse() { Status = TicketStatus.Ok };
    }
}
