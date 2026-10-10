using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// Runs an app's rules (ILogic<T> in its shared classes) against one signal, inside the brain's session:
// each brain's rules load into their own context, so statics never cross brains. Anything that fails
// while loading or running a rule is that rule's rejection; a rule never fails open.
internal sealed class RuleJudge(BehaviorLoader loader)
{
    private const int Capacity = 256;
    private const string Library = "rules";

    private readonly ConcurrentDictionary<(string Session, string Hash), Lazy<Task<RuleSet>>> _loaded = new();
    private readonly ConcurrentQueue<(string Session, string Hash)> _order = new();

    public async Task<JudgeAnswer> JudgeAsync(string session, JudgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = Type.GetType(request.SignalType, throwOnError: false);
        if (type is null || !typeof(Signal).IsAssignableFrom(type))
        { return new([new(Library, false, $"this sandbox does not know the signal {request.SignalType}")]); }
        var signal = (Signal)request.Signal.Deserialize(type, RuntimeProtocol.Json)!;
        var rules = await Load(session, request).WaitAsync(cancellationToken).ConfigureAwait(false);
        // Rules are user code: they run off the request thread, and an abandoned one only costs this session a thread.
        var verdicts = await Task.Run(() => rules.Judge(signal), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(verdicts);
    }

    private Task<RuleSet> Load(string session, JudgeRequest request)
    {
        var key = (session, request.Hash);
        var lazy = _loaded.GetOrAdd(key, _ => new(() => RuleSet.LoadAsync(loader, session, request, CancellationToken.None)));
        if (lazy.IsValueCreated) { return lazy.Value; }
        var task = lazy.Value;
        _order.Enqueue(key);
        while (_order.Count > Capacity && _order.TryDequeue(out var evicted))
        {
            if (_loaded.TryRemove(evicted, out var gone) && gone.Value.IsCompletedSuccessfully) { gone.Value.Result.Unload(); }
        }
        return task;
    }

    private sealed class RuleSet(BehaviorLoader.BehaviorContext? context, (Type Type, Type Signal, MethodInfo Validate)[] rules, JudgeVerdict[] failures)
    {
        public static async Task<RuleSet> LoadAsync(BehaviorLoader loader, string session, JudgeRequest request, CancellationToken cancellationToken)
        {
            byte[] image;
            try { image = await loader.CachedAsync(request.Hash, request.Assembly, verify: true, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(null, [], [new(Library, false, "the rules could not be fetched: " + error.Message)]); }
            var context = new BehaviorLoader.BehaviorContext($"rules {session} {request.Hash[..12]}");
            Type[] types;
            var failures = new List<JudgeVerdict>();
            try
            {
                using var stream = new MemoryStream(image);
                var assembly = context.LoadFromStream(stream);
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException partial)
                {
                    types = [.. partial.Types.OfType<Type>()];
                    failures.Add(new(Library, false, "a rule could not load: " + string.Join("; ", partial.LoaderExceptions.OfType<Exception>().Select(error => error.Message))));
                }
            }
            catch (Exception error)
            { return new(context, [], [new(Library, false, "the rules could not load: " + error.Message)]); }
            var rules = types
                .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
                .SelectMany(type => type.GetInterfaces()
                    .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ILogic<>))
                    .Select(contract => (type, contract.GetGenericArguments()[0], contract.GetMethod(nameof(ILogic<Signal>.Validate))!)))
                .ToArray();
            return new(context, rules, [.. failures]);
        }

        public JudgeVerdict[] Judge(Signal signal)
        {
            var verdicts = new List<JudgeVerdict>(failures);
            foreach (var (type, judged, validate) in rules.Where(rule => rule.Signal.IsInstanceOfType(signal)))
            {
                try
                {
                    var verdict = (Verdict?)validate.Invoke(Activator.CreateInstance(type), [signal])
                        ?? throw new InvalidOperationException("the rule returned no verdict");
                    verdicts.Add(new(type.Name, verdict.Allowed, verdict.Reason));
                }
                catch (Exception error)
                {
                    var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                    verdicts.Add(new(type.Name, false, "the rule failed: " + cause.Message));
                }
            }
            return [.. verdicts];
        }

        public void Unload() => context?.Unload();
    }
}
