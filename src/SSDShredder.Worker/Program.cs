using SSDShredder.Worker.Cli;
using SSDShredder.Worker.Hosting;
using SSDShredder.Worker.Protocol;

namespace SSDShredder.Worker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!CommandLine.TryParse(args, out ParsedCommand? cmd, out string error) || cmd is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(CommandLine.Usage);
            return (int)ExitCode.Invalid;
        }

        try
        {
            WorkerEnvironment env = WorkerEnvironment.Create();
            if (cmd.Verb == Verb.Ipc) return (int)HelperMode.Run(cmd, env);

            using Stream stdout = Console.OpenStandardOutput();
            return (int)CliHost.Run(cmd, env, stdout, Console.In, Console.Error, interactive: !Console.IsInputRedirected);
        }
        catch (Exception e)
        {
            // Redacted: type and code only, no paths or messages.
            Console.Error.WriteLine($"internal error: {e.GetType().Name} (0x{e.HResult:X8})");
            return (int)ExitCode.Failed;
        }
    }
}
