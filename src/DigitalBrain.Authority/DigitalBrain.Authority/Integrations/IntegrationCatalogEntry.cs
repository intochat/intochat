using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Authority.Integrations;

internal sealed record IntegrationCatalogEntry(string Id, string DisplayName, string Status, string[] MissingFields);
