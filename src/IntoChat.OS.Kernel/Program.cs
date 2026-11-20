using IntoChat.OS.Kernel;

var builder = WebApplication.CreateBuilder(args);
builder.AddIntoChatOSKernel();

var app = builder.Build();
app.MapIntoChatOSKernel();

Environment.ExitCode = await app.RunIntoChatOSKernelAsync(args);
