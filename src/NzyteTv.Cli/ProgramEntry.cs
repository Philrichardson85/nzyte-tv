using System.Runtime.InteropServices;

namespace NzyteTv.Cli;

public static class ProgramEntry
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using PosixSignalRegistration? terminationRegistration = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });

        return await CliApplication.RunAsync(args, cancellation.Token).ConfigureAwait(false);
    }
}
