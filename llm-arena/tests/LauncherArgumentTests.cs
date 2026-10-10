using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenRA.ArenaLauncher;

static class LauncherArgumentTests
{
	static int Main(string[] args)
	{
		if (args.Length >= 2 && args[0] == "--echo")
		{
			File.WriteAllLines(args[1], args.Skip(2));
			return 0;
		}
		var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		var paths = new LaunchPaths(Path.Combine(directory, "root with spaces") + "\\");
		var output = Path.Combine(directory, "argument-test-result.txt");
		Check(paths.GameInfo(), new[] { "Engine.EngineDir=" + paths.Root, "Engine.SupportDir=" + paths.Support,
			"Game.Mod=ra", "Graphics.Mode=Windowed", "Graphics.WindowedSize=1280,800" }, output, directory);
		var stopFile = Path.Combine(directory, "stop file with spaces");
		Check(paths.AgentInfo(stopFile, 123), new[] { "-u", Path.Combine(paths.Arena, "launcher_runner.py"),
			"--stop-file", stopFile, "--parent-pid", "123", "--host", "https://ollama.com", "--watch-games" }, output, directory);
		Console.WriteLine("OK: game and agent arguments survive Windows parsing, spaces and trailing backslashes.");
		return 0;
	}

	static void Check(ProcessStartInfo info, string[] expected, string output, string directory)
	{
		info.FileName = Assembly.GetExecutingAssembly().Location;
		info.WorkingDirectory = directory;
		info.Arguments = "--echo " + LaunchPaths.Quote(output) + " " + info.Arguments;
		using (var process = Process.Start(info))
		{
			if (!process.WaitForExit(5000)) throw new Exception("Argument test child did not exit.");
			if (process.ExitCode != 0) throw new Exception("Argument test child failed.");
		}
		if (!File.ReadAllLines(output).SequenceEqual(expected))
			throw new Exception("Windows argument boundaries were not preserved.");
	}
}
