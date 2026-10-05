using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

public sealed record AppRuntimeRequest(
    string AppKey,
    Guid InvocationId,
    string Operation,
    string Input,
    PackageContent Content,
    IReadOnlyDictionary<string, string> Settings);
