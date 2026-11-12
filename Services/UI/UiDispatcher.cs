using Microsoft.UI.Dispatching;

namespace Talky.Services.UI;

/// <summary>Marshals work onto the UI thread so services can stay UI-agnostic.</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Fire-and-forget on the UI thread (runs inline if already there).</summary>
    void Post(Action action);

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<Task<T>> function);
}

public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Post(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }

    public Task InvokeAsync(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetCanceled();
        }

        return completion.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> function)
    {
        if (queue.HasThreadAccess)
        {
            return function();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await function());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetCanceled();
        }

        return completion.Task;
    }
}
