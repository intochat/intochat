using System.Collections.Concurrent;
using System.Reflection;

namespace DigitalBrain.Invocation;

/// <summary>Awaits a task known only as a <see cref="Task"/> and reads its result without reflecting over Result on every call.</summary>
public static class TaskBoxing
{
    private static readonly ConcurrentDictionary<Type, Func<Task, Task<object?>>> Boxes = new();
    private static readonly MethodInfo BoxMethod = typeof(TaskBoxing).GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Awaits <paramref name="task"/> and boxes its result when <paramref name="resultType"/> is non-null.</summary>
    public static async Task<object?> ResultAsync(Task task, Type? resultType)
    {
        if (resultType is null) { await task.ConfigureAwait(true); return null; }
        if (resultType.IsCollectible)
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

    private static async Task<object?> Box<T>(Task task) => await ((Task<T>)task).ConfigureAwait(true);
}
