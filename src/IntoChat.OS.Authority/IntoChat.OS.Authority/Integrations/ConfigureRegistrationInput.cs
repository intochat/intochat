using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Integrations;

internal sealed record ConfigureRegistrationInput(Dictionary<string, string>? Values);
