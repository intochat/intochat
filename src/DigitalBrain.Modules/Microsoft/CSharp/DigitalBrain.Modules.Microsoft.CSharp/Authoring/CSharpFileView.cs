using System.ComponentModel;
using System.Reflection;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files;
using ModelContextProtocol.Server;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

public sealed record CSharpFileView(CSharpDescription Description, CSharpFileSnapshot File, string? Logs = null);
