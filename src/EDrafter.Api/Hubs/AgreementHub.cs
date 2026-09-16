using Microsoft.AspNetCore.SignalR;

namespace EDrafter.Api.Hubs;

/// <summary>
/// Pushes status changes to the browser. The flow has a wait measured in hours —
/// order to stamp is 1 working hour to 2 working days, gated behind a human at
/// eDrafter accepting the order — so without this the user sits refreshing.
/// </summary>
public sealed class AgreementHub : Hub
{
    public Task Subscribe(string agreementId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, agreementId);
}
