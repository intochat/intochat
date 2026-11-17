using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Kernel.DevUI;

// Microsoft Agent Framework DevUI on the kernel, for local development only: /devui chats with the caller's current Ino
// conversation through the OpenAI Responses and Conversations endpoints. It sits behind the kernel's own sign-in (the same
// owner session as the shell) and DevUI's loopback check; it is never mapped outside Development or when switched off.
public static class DevUIHosting
{
    public const string EnabledKey = "IntoChatOS:DevUI:Enabled";
    public const string Path = "/devui";
    public const string InoAgent = "ino";

    public static bool IsEnabled(IHostApplicationBuilder builder)
        => builder.Environment.IsDevelopment() && builder.Configuration.GetValue(EnabledKey, true);

    public static void AddDevUIForIno(this IHostApplicationBuilder builder)
    {
        if (!IsEnabled(builder)) { return; }
        builder.AddAIAgent(InoAgent, (services, name) => new ChatClientAgent(new NeuronAgentChatClient(services), name: name,
            description: "Your current Ino conversation (the same one the shell shows)."));
        builder.AddWorkflow(ApprovalDemoWorkflow.Name, (_, _) => ApprovalDemoWorkflow.Build());
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();
    }

    public static void MapDevUIForIno(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue(EnabledKey, true)) { return; }
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
        RunsPage.Map(app);
    }
}
