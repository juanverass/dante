namespace Dante.ProcessProbe;

public static class ProbeMarker { }

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args[0])
        {
            case "echo":
                Console.WriteLine(args[1]);
                Console.Error.WriteLine("probe stderr");
                return 0;
            case "fail":
                Console.Error.WriteLine("probe failed");
                return 7;
            case "cwd":
                Console.WriteLine(Environment.CurrentDirectory);
                return 0;
            case "env":
                Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "<unset>");
                return 0;
            case "wait":
                Console.WriteLine("ready");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            default:
                return 2;
        }
    }
}
