using System.Collections.Concurrent;
using System.Reflection;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// Awaits a task known only as a Task and reads its result without reflecting over the Result property on every call.
public static class TaskBoxing
{
    private static readonly ConcurrentDictionary<Type, Func<Task, Task<object?>>> Boxes = new();
    private static readonly MethodInfo BoxMethod = typeof(TaskBoxing).GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)!;

    // returnType is the declared return type: Task, Task<T>, or ValueTask<T> when task is its AsTask(). A non-generic type yields null.
    public static async Task<object?> ResultAsync(Task task, Type returnType)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(returnType);
        if (!returnType.IsGenericType) { await task.ConfigureAwait(false); return null; }
        var box = Boxes.GetOrAdd(returnType.GetGenericArguments()[0], static result
            => BoxMethod.MakeGenericMethod(result).CreateDelegate<Func<Task, Task<object?>>>());
        return await box(task).ConfigureAwait(false);
    }

    private static async Task<object?> Box<T>(Task task) => await ((Task<T>)task).ConfigureAwait(false);
}
