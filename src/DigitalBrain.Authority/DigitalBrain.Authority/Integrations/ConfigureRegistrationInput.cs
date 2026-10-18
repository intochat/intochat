using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Authority.Integrations;

internal sealed record ConfigureRegistrationInput(Dictionary<string, string>? Values);
