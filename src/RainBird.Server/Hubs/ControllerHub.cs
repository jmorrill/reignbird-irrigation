using Microsoft.AspNetCore.SignalR;

namespace RainBird.Server.Hubs;

/// <summary>
/// Pushes live controller state to connected clients.
///
/// Clients join a group per controller so a browser watching one controller is not
/// woken by another's polls. The hub also tracks whether anyone is listening at all,
/// which the polling service uses to decide between its fast and idle cadences —
/// there is no reason to hammer a small embedded device when nobody is looking.
/// </summary>
public class ControllerHub : Hub
{
    private static int _connectionCount;

    public static bool HasListeners => Volatile.Read(ref _connectionCount) > 0;

    public static string GroupFor(int controllerId) => $"controller-{controllerId}";

    public async Task Subscribe(int controllerId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(controllerId));

    public async Task Unsubscribe(int controllerId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(controllerId));

    public override Task OnConnectedAsync()
    {
        Interlocked.Increment(ref _connectionCount);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Interlocked.Decrement(ref _connectionCount);
        return base.OnDisconnectedAsync(exception);
    }
}
