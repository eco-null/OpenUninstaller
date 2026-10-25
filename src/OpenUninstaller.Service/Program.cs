using System.Text;
using OpenUninstaller.Core.Services;
using OpenUninstaller.Core.Support;

namespace OpenUninstaller.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var console = args.Any(a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase));
        var once = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));
        var forceService = args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

        var paths = AppPaths.CreateDefault();
        paths.EnsureDirectories();

        if (!console && ServiceHost.TryRun(
                BackgroundServiceManager.DefaultServiceName,
                cancellationToken => Worker.RunAsync(paths, once: false, cancellationToken)))
        {
            return 0;
        }

        if (forceService)
        {
            return 1;
        }

        Console.WriteLine("OpenUninstaller maintenance service — console mode.");
        Console.WriteLine("The Windows service is normally started by the Service Control Manager.");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Worker.RunAsync(paths, once, cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
