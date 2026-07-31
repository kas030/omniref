using System.IO.Pipes;
using System.IO;
using System.Text.Json;

namespace OmniRef.Infrastructure.Windows.SingleInstance;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\OmniRef-46B97118-1F70-4E5D-90AE-2D61A04A6A61";
    private const string PipeName = "OmniRef-46B97118-1F70-4E5D-90AE-2D61A04A6A61";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listener;
    private bool _ownsMutex;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
    }

    public bool IsPrimary => _ownsMutex;

    public event EventHandler<IReadOnlyList<string>>? ActivationReceived;

    public void StartListening()
    {
        if (!IsPrimary || _listener is not null)
        {
            return;
        }

        _listener = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public static async Task<bool> SendActivationAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(client, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var arguments = await JsonSerializer.DeserializeAsync<string[]>(
                        server,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (arguments is not null)
                {
                    ActivationReceived?.Invoke(this, arguments);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }
}
