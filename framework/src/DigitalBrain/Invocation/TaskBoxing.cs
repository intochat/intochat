using System.Collections.Concurrent;
using System.Reflection;

namespace DigitalBrain.Invocation;

/// <summary>Awaits a task known only as a <see cref="Task"/> and reads its result without reflecting over Result on every call.</summary>
public static class TaskBoxing
{
    private static readonly ConcurrentDictionary<Type, Func<Task, Task<object?>>> Boxes = new();
    private static readonly ConcurrentDictionary<Type, Func<object, CancellationToken, Task<object?>>> ValueTaskBoxes = new();
    private static readonly MethodInfo BoxMethod = typeof(TaskBoxing).GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo BoxValueTaskMethod = typeof(TaskBoxing).GetMethod(nameof(BoxValueTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Awaits <paramref name="task"/> and boxes its result when <paramref name="resultType"/> is non-null.</summary>
    public static async Task<object?> ResultAsync(Task task, Type? resultType)
    {
        if (resultType is null) { await task.ConfigureAwait(true); return null; }
        if (resultType.IsCollectible || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            await task.ConfigureAwait(true);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }

        var box = Boxes.GetOrAdd(resultType, static result => BoxMethod.MakeGenericMethod(result).CreateDelegate<Func<Task, Task<object?>>>());
        return await box(task).ConfigureAwait(true);
    }

    /// <summary>Awaits <paramref name="task"/> given the reflection <paramref name="returnType"/> of the invoked method (<c>Task</c> or <c>Task&lt;T&gt;</c>).</summary>
    public static Task<object?> ReturnResultAsync(Task task, Type returnType)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(returnType);
        return ResultAsync(task, returnType.IsGenericType ? returnType.GetGenericArguments()[0] : null);
    }

    /// <summary>
    /// Awaits a dynamic invocation result (<c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c>, <c>ValueTask&lt;T&gt;</c>, or a synchronous value)
    /// and returns the boxed value, or <see langword="null"/> for void/non-generic async methods.
    /// </summary>
    public static async Task<object?> UnwrapAsync(object? rawResult, Type returnType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(returnType);

        if (rawResult is Task task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!returnType.IsGenericType)
            {
                return null;
            }

            return await ResultAsync(task, returnType.GetGenericArguments()[0]).ConfigureAwait(false);
        }

        if (rawResult is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return null;
        }

        if (rawResult is not null
            && rawResult.GetType() is { IsGenericType: true } valueTaskType
            && valueTaskType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = valueTaskType.GetGenericArguments()[0];
            if (resultType.IsCollectible || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            {
                var asTask = (Task)valueTaskType.GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)!.Invoke(rawResult, null)!;
                await asTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                return asTask.GetType().GetProperty("Result")?.GetValue(asTask);
            }

            var box = ValueTaskBoxes.GetOrAdd(
                resultType,
                static result => BoxValueTaskMethod.MakeGenericMethod(result).CreateDelegate<Func<object, CancellationToken, Task<object?>>>());
            return await box(rawResult, cancellationToken).ConfigureAwait(false);
        }

        return rawResult;
    }

    private static async Task<object?> Box<T>(Task task) => await ((Task<T>)task).ConfigureAwait(true);

    private static async Task<object?> BoxValueTask<T>(object raw, CancellationToken cancellationToken)
    {
        var task = ((ValueTask<T>)raw).AsTask();
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }
}
