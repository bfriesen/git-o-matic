using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibGit2Sharp;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace GitOMatic
{
    class Program
    {
        private const int _timerDelay = 5000;
        private static Timer _timer;
        private static int _isProcessing;
        private static FileSystemWatcher _watcher;

        private static string _configPath;
        private static dynamic _config;

        static void Main(string[] args)
        {
            _configPath = args[0];
            var configJson = File.ReadAllText(_configPath);

            var serializer = new JsonSerializer();

            _config = serializer.Deserialize(configJson);

            _timer = new Timer(Elapsed, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_config.Git.Path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;

            _watcher.EnableRaisingEvents = true;

            _timer.Change(_timerDelay, Timeout.Infinite);

            Console.WriteLine("Enter \'q\' to quit.");
            Console.WriteLine("Enter anything else to set the current message.");

            string message;

            while ((message = Console.ReadLine()) != "q")
            {
                _config.Git.Message = message;
                WriteConfig();
                Console.WriteLine("Message set to: {0}", message);
            }
        }

        private static void WriteConfig()
        {
            using (var writer = new StreamWriter(_configPath))
            {
                writer.WriteLine("{");

                writer.WriteLine("    \"MSBuild\" : {");

                writer.Write("        \"ProjectFile\" : \"");
                writer.Write(((string)_config.MSBuild.ProjectFile).Replace("\\", "\\\\").Replace("\"", "\\\""));
                writer.WriteLine("\",");

                writer.WriteLine("        \"Options\" : {");

                writer.Write("            \"Configuration\" : \"");
                writer.Write(_config.MSBuild.Options.Configuration);
                writer.WriteLine("\",");

                writer.Write("            \"Platform\" : \"");
                writer.Write(_config.MSBuild.Options.Platform);
                writer.WriteLine("\",");

                writer.Write("            \"OutputPath\" : \"");
                writer.Write(((string)_config.MSBuild.Options.OutputPath).Replace("\\", "\\\\").Replace("\"", "\\\""));
                writer.WriteLine("\"");

                writer.WriteLine("        }");

                writer.WriteLine("    },");

                writer.Write("    \"TestAssemblyFileNames\" : [ ");
                writer.Write(string.Join(", ", ((object[])_config.TestAssemblyFileNames).Select(x => "\"" + x + "\"")));
                writer.WriteLine(" ],");

                writer.WriteLine("    \"Git\" : {");

                writer.Write("        \"Path\" : \"");
                writer.Write(((string)_config.Git.Path).Replace("\\", "\\\\").Replace("\"", "\\\""));
                writer.WriteLine("\",");

                writer.Write("        \"Message\" : \"");
                writer.Write(((string)_config.Git.Message).Replace("\\", "\\\\").Replace("\"", "\\\""));
                writer.WriteLine("\"");

                writer.WriteLine("    }");

                writer.Write("}");
            }
        }

        private static void OnChanged(object source, FileSystemEventArgs e)
        {
            if (e.FullPath.Contains("/obj")
                || e.FullPath.Contains(@"\obj")
                || e.FullPath.Contains("/.git")
                || e.FullPath.Contains(@"\.git")
                || e.FullPath.EndsWith(@".suo"))
            {
                return;
            }

            _timer.Change(_timerDelay, Timeout.Infinite);
        }

        private static void Elapsed(object state)
        {
            // Only one thread is allowed to be processing the file changes
            // at any given time. But threads shouldn't block if they can't
            // obtain the lock - just try again later.

            if (TryGetLock())
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    ProcessFileChanges(_config);
                }
                finally
                {
                    ReleaseLock();
                    _watcher.EnableRaisingEvents = true;
                }
            }
            else
            {
                _timer.Change(_timerDelay, Timeout.Infinite);
            }
        }

        private static bool TryGetLock()
        {
            return Interlocked.Exchange(ref _isProcessing, 1) == 0;
        }

        private static void ReleaseLock()
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }

        private static void ProcessFileChanges(dynamic config)
        {
            Console.WriteLine("Starting build...");

            var buildSuccess = TryBuild(config.MSBuild);

            if (!buildSuccess)
            {
                Console.WriteLine("Build failed.");
                return;
            }

            Console.WriteLine("Build successful. Starting tests...");

            var testsSuccess = TryTests(config.MSBuild.Options.OutputPath, config.TestAssemblyFileNames);

            if (!testsSuccess)
            {
                Console.WriteLine("Tests failed.");
                return;
            }

            Console.WriteLine("Tests successful. Committing to git...");

            var gitCommitSuccess = TryCommit(config.Git.Path, config.Git.Message);

            if (!gitCommitSuccess)
            {
                Console.WriteLine("Git commit failed.");
                return;
            }

            Console.WriteLine("Git commit successful.");
        }

        private static bool TryBuild(dynamic build)
        {
            return TryBuild(build, "Clean") && TryBuild(build, "Build");
        }

        private static bool TryBuild(dynamic build, string target)
        {
            try
            {
                var props = GetDictionary(build.Options);

                var loggers = new ILogger[] { new ConsoleLogger() };

                const ToolsetDefinitionLocations toolsetDefinitionLocations =
                    ToolsetDefinitionLocations.ConfigurationFile | ToolsetDefinitionLocations.Registry;

                var projectCollection = new ProjectCollection(props, loggers,
                    toolsetDefinitionLocations);
                var request = new BuildRequestData(build.ProjectFile, props, null, new[] { target }, null);

                var parameters = new BuildParameters(projectCollection)
                {
                    MaxNodeCount = 1,
                    ToolsetDefinitionLocations = toolsetDefinitionLocations,
                    Loggers = projectCollection.Loggers
                };

                var result = BuildManager.DefaultBuildManager.Build(parameters, request);

                return result.OverallResult == BuildResultCode.Success;
            }
            catch
            {
                return false;
            }
        }

        private static IDictionary<string, string> GetDictionary(IDictionary<string, object> options)
        {
            return options.ToDictionary(x => x.Key, x => x.Value.ToString());
        }

        private static bool TryTests(string buildOutputPath, IEnumerable<object> testAssemblyFilePaths)
        {
            return TryTests(testAssemblyFilePaths.Select(x => Path.Combine(buildOutputPath, (string)x)).ToArray());
        }

        private static bool TryTests(string[] testAssemblyFilePaths)
        {
            var result = NUnit.ConsoleRunner.Runner.Main(testAssemblyFilePaths);
            return result == 0;
        }

        private static bool TryCommit(string gitRepoPath, string message)
        {
            try
            {
                using (var r = new Repository(gitRepoPath))
                {
                    var status = r.RetrieveStatus();

                    var toStage =
                        status.Where(s =>
                            (s.State & FileStatus.Untracked) != 0
                            || (s.State & FileStatus.Modified) != 0
                            || (s.State & FileStatus.RenamedInWorkDir) != 0);

                    var toRemove =
                        status.Where(s =>
                            (s.State & FileStatus.Missing) != 0);

                    foreach (var item in toStage)
                    {
                        r.Stage(item.FilePath);
                    }

                    foreach (var item in toRemove)
                    {
                        r.Remove(item.FilePath);
                    }

                    r.Commit(message);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }
    }
}
