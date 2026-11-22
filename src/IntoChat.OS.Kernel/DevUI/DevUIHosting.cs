using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Kernel.DevUI;

// The ino agent over AG-UI for every client, and Microsoft Agent Framework DevUI on the kernel for local development only: /devui chats with the caller's current Ino
// conversation through the OpenAI Responses and Conversations endpoints. It sits behind the kernel's own sign-in (the same
// owner session as the shell) and DevUI's loopback check; it is never mapped outside Development or when switched off.
public static class DevUIHosting
{
    public const string EnabledKey = "IntoChatOS:DevUI:Enabled";
    public const string Path = "/devui";
    public const string InoAgent = "ino";

    public static bool IsEnabled(IHostApplicationBuilder builder)
        => IsEnabled(builder.Environment, builder.Configuration);

    private static bool IsEnabled(IHostEnvironment environment, IConfiguration configuration)
        => (environment.IsDevelopment() || string.Equals(configuration["IntoChatOS:Topology"], "local", StringComparison.OrdinalIgnoreCase))
            && configuration.GetValue(EnabledKey, true);

    public const string AguiPath = "/agui/ino";

    public static void AddDevUIForIno(this IHostApplicationBuilder builder)
    {
        // The ino agent and its AG-UI endpoint serve every client (the Flutter shell among them), behind the kernel's sign-in.
        builder.AddAIAgent(InoAgent, (services, name) => new ChatClientAgent(new NeuronAgentChatClient(services), name: name,
            description: "Your current Ino conversation (the same one the shell shows)."));
        builder.Services.AddAGUIServer();
        if (!IsEnabled(builder)) { return; }
        builder.Services.AddSingleton(new IntoChat.OS.Modules.AI.Workflows.WorkflowDefinition(ApprovalDemoWorkflow.Name,
            "Shouts its input and waits for a person to approve before it finishes.", ApprovalDemoWorkflow.Build));
        builder.AddWorkflow(ApprovalDemoWorkflow.Name, (_, _) => ApprovalDemoWorkflow.Build());
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();
    }

    public static void MapDevUIForIno(this WebApplication app)
    {
        app.MapAGUIServer(InoAgent, AguiPath);
        if (!IsEnabled(app.Environment, app.Configuration)) { return; }
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI().AllowAnonymous();
        RunsPage.Map(app);
        BrainPage.Map(app);
    }
}
