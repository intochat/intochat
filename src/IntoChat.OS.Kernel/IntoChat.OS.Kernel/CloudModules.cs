using DigitalBrain.Runtime.Orleans;
using IntoChat.Cloud.Auth;
using IntoChat.Cloud.Hosting;
using IntoChat.Cloud.Lobby;
using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Organisations;

namespace IntoChat.OS.Kernel;

// The cloud modules this host composes. Phase 10 adds the circle module for local+cloud and cloud; this list is cloud only.
public static class CloudModules
{
    public static IReadOnlyList<Type> For(Topology topology) => topology == Topology.Cloud
        ? [typeof(AuthModule), typeof(MailModule), typeof(OrganisationsModule), typeof(LobbyModule), typeof(HostingModule)]
        : [];
}
