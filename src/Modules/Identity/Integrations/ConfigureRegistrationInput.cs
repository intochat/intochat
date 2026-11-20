using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Identity.Integrations;

internal sealed record ConfigureRegistrationInput(Dictionary<string, string>? Values);
