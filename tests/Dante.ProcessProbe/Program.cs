using System.Diagnostics;

namespace Dante.ProcessProbe;

public static class ProbeMarker { }

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args[0])
        {
            case "fake-claude":
                return FakeClaude.Run(args);
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
            case "env-until-eof":
                Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "<unset>");
                while (Console.In.ReadLine() is not null) { }
                return 0;
            case "wait":
                Console.WriteLine("ready");
                await Task.Delay(args.Length > 1 ? int.Parse(args[1]) : Timeout.Infinite);
                return 0;
            case "interactive":
                // Echoes each stdin line until "exit"/"fail" or EOF; proves streaming and repeated input.
                Console.WriteLine("ready");
                while (Console.In.ReadLine() is { } line)
                {
                    switch (line)
                    {
                        case "exit":
                            return 0;
                        case "fail":
                            Console.Error.WriteLine("probe failed");
                            return 3;
                        default:
                            Console.WriteLine("echo:" + line);
                            break;
                    }
                }

                Console.WriteLine("stdin closed");
                return 0;
            case "burst":
                for (var index = 0; index < int.Parse(args[1]); index++)
                {
                    Console.WriteLine($"line {index}");
                }

                return 0;
            case "spawn":
                // Starts a grandchild that never exits and reports its PID; args[1] is the runtimeconfig path.
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                       {
                           ArgumentList = { "exec", "--runtimeconfig", args[1], typeof(Program).Assembly.Location, "wait" },
                           UseShellExecute = false
                       }))
                {
                    Console.WriteLine(child!.Id);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            case "spawn-on-command":
            case "spawn-and-exit":
            case "spawn-until-eof":
                if (args[0] == "spawn-on-command")
                {
                    Console.WriteLine("ready");
                    if (Console.In.ReadLine() != "spawn") return 2;
                }
                // Múltiplos descendentes e saída natural opcional para os testes de limpeza.
                var count = args.Length > 2 ? int.Parse(args[2]) : 1;
                for (var index = 0; index < count; index++)
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        ArgumentList = { "exec", "--runtimeconfig", args[1], typeof(Program).Assembly.Location, "wait" },
                        UseShellExecute = false
                    };
                    if (args.Length > 3) start.ArgumentList.Add(args[3]);
                    using var child = Process.Start(start);
                    Console.WriteLine(child!.Id);
                }

                if (args[0] == "spawn-and-exit") return 0;
                while (Console.In.ReadLine() is not null)
                {
                }

                return 0;
            case "fake-codex":
                return FakeCodex.Run(args);
            default:
                return 2;
        }
    }
}
