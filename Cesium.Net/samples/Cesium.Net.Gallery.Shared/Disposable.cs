namespace CesiumNet.Gallery;

/// <summary>Creates <see cref="IAsyncDisposable"/> callbacks for <see cref="SampleContext.Resources"/> (e.g. event unsubscription).</summary>
public static class Disposable
{
    public static IAsyncDisposable Create(Action onDispose) => new ActionDisposable(onDispose);

    private sealed class ActionDisposable(Action onDispose) : IAsyncDisposable
    {
        private Action? _onDispose = onDispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
