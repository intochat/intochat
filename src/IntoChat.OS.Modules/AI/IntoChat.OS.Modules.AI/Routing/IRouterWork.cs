namespace IntoChat.OS.Modules.AI.Routing;

// Ask's answer runs as a one-way message to the router itself, so the poster returns at once.
[Alias("ai.router-work")]
internal interface IRouterWork : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Decide(Guid id);
}
