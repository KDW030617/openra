using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenRA.ArenaLauncher
{
	sealed class LaunchPaths
	{
		public readonly string Root;
		public readonly string Arena;
		public readonly string Runtime;
		public readonly string Support;
		public readonly string Logs;

		public LaunchPaths(string root)
		{
			Root = Path.GetFullPath(root);
			Arena = Path.Combine(Root, "llm-arena");
			Runtime = Path.Combine(Arena, ".local", "dotnet");
			Support = Path.Combine(Arena, ".local", "openra-support");
			Logs = Path.Combine(Arena, ".local", "launcher");
		}

		public string ApiKey
		{
			get
			{
				var key = Environment.GetEnvironmentVariable("OLLAMA_API_KEY", EnvironmentVariableTarget.User);
				if (string.IsNullOrWhiteSpace(key))
					key = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
				return (key ?? "").Trim();
			}
		}

		public void Validate()
		{
			Require(Path.Combine(Root, "bin", "OpenRA.exe"), "The OpenRA game build is missing.");
			Require(Path.Combine(Arena, ".venv", "Scripts", "python.exe"), "Python is missing. Open llm-arena/setup.cmd first.");
			Require(Path.Combine(Arena, "launcher_runner.py"), "Keep this EXE in the OpenRA folder. Use a shortcut to move it.");
			Require(Path.Combine(Runtime, "dotnet.exe"), "The local .NET runtime is missing.");
			var baseFiles = new[] { "allies", "conquer", "hires", "interior", "local", "lores", "russian", "snow", "sounds", "speech", "temperat" };
			foreach (var file in baseFiles)
				Require(Path.Combine(Support, "Content", "ra", "v2", file + ".mix"), "Red Alert game data is missing.");
			Require(Path.Combine(Support, "Content", "ra", "v2", "expand", "expand2.mix"), "Red Alert expansion data is missing.");
			Require(Path.Combine(Support, "Content", "ra", "v2", "cnc", "desert.mix"), "Red Alert terrain data is missing.");
			if (ApiKey.Length == 0)
				throw new InvalidOperationException("OLLAMA_API_KEY is not configured. Open llm-arena/setup.cmd to enter your key.");
			Directory.CreateDirectory(Logs);
		}

		public ProcessStartInfo GameInfo()
		{
			var info = BaseInfo(Path.Combine(Root, "bin", "OpenRA.exe"));
			info.Arguments = Quote("Engine.EngineDir=" + Root) + " " + Quote("Engine.SupportDir=" + Support)
				+ " Game.Mod=ra Graphics.Mode=Windowed Graphics.WindowedSize=1280,800";
			info.EnvironmentVariables["DOTNET_ROOT"] = Runtime;
			info.EnvironmentVariables["DOTNET_ROOT_X64"] = Runtime;
			// CreateNoWindow suppresses the console; do not hide the SDL game window.
			return info;
		}

		public ProcessStartInfo AgentInfo(string stopFile, int parentId)
		{
			var info = BaseInfo(Path.Combine(Arena, ".venv", "Scripts", "python.exe"));
			info.Arguments = "-u " + Quote(Path.Combine(Arena, "launcher_runner.py"))
				+ " --stop-file " + Quote(stopFile) + " --parent-pid " + parentId + " --host https://ollama.com --watch-games";
			info.WorkingDirectory = Arena;
			info.EnvironmentVariables["OLLAMA_API_KEY"] = ApiKey;
			info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
			return info;
		}

		ProcessStartInfo BaseInfo(string executable)
		{
			return new ProcessStartInfo(executable)
			{
				WorkingDirectory = Root,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};
		}

		static void Require(string path, string message)
		{
			if (!File.Exists(path))
				throw new FileNotFoundException(message, path);
		}

		public static string Quote(string value)
		{
			// Windows argv parsing requires backslashes before quotes to be doubled.
			var result = new StringBuilder("\"");
			var slashes = 0;
			foreach (var character in value)
			{
				if (character == '\\') { slashes++; continue; }
				result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
				result.Append(character);
				slashes = 0;
			}
			result.Append('\\', slashes * 2);
			return result.Append('"').ToString();
		}
	}
}
