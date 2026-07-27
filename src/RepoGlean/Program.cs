namespace RepoGlean;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new ConsoleCancellation();
        return await RepoGleanApp.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
    }

    private sealed class ConsoleCancellation : IDisposable
    {
        private readonly CancellationTokenSource source = new();

        public ConsoleCancellation()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            source.Dispose();
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            source.Cancel();
        }
    }
}
