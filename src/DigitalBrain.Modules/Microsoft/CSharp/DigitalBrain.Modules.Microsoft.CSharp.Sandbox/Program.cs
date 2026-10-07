using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Sandbox;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddOptions<SandboxOptions>().BindConfiguration(SandboxOptions.SectionName);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = RuntimeProtocol.Json.PropertyNamingPolicy);
builder.Services.AddHttpClient<BehaviorLoader>();
builder.Services.AddSingleton<Sessions>();

var app = builder.Build();
app.Logger.LogInformation("Sandbox host carries contracts {Fingerprint}", Sessions.Fingerprint.Replace('\n', ' '));
app.MapSandbox();
await app.RunAsync();
