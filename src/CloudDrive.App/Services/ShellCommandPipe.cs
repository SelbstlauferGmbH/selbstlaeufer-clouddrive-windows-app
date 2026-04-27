using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudDrive.App.Services;

public sealed class ShellCommandServer : IDisposable
{
    private readonly ILogger<ShellCommandServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public ShellCommandServer(ILogger<ShellCommandServer> logger)
    {
        _logger = logger;
    }

    public event Func<ShellCommand, Task>? CommandReceived;

    public void Start()
    {
        _serverTask ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    GetPipeName(),
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var json = await reader.ReadToEndAsync(ct);
                var command = ShellCommand.FromJson(json);
                if (command != null && CommandReceived != null)
                    await CommandReceived.Invoke(command);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shell command pipe failed");
            }
        }
    }

    public static string GetPipeName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeSid = string.Concat(sid.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        return $"CloudDrive.ShellCommands.{safeSid}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch { }
        _cts.Dispose();
    }
}

public static class ShellCommandClient
{
    public static async Task<bool> TrySendAsync(ShellCommand command, TimeSpan timeout)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            await using var pipe = new NamedPipeClientStream(
                ".",
                ShellCommandServer.GetPipeName(),
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutCts.Token);
            var bytes = Encoding.UTF8.GetBytes(command.ToJson());
            await pipe.WriteAsync(bytes, timeoutCts.Token);
            await pipe.FlushAsync(timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
