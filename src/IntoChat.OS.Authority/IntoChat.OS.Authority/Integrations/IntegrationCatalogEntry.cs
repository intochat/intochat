using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Integrations;

internal sealed record IntegrationCatalogEntry(string Id, string DisplayName, string Status, string[] MissingFields);
