﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibGit2Sharp;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace FileWatcherSpike
{
    class Program
    {
        private const int _timerDelay = 5000;
        private static Timer _timer;
        private static int _isProcessing;
        private static FileSystemWatcher _watcher;

        static void Main(string[] args)
        {
            // this should be loaded from a file specified in args
            const string configJson = 
@"{
    ""MSBuild"" : {
        ""ProjectFile"" : ""C:\\Users\\bfriesen\\Documents\\visual studio 2013\\Projects\\JsonParserSpike\\JsonParserSpike.sln"",
        ""Options"" : {
            ""Configuration"" : ""Release"",
            ""Platform"" : ""Any CPU"",
            ""OutputPath"" : ""C:\\Temp\\JsonParserSpike""
        }
    },
    ""TestAssemblyFileNames"" : [ ""JsonParserSpike.Tests.dll"" ],
    ""Git"" : {
        ""Path"" : ""C:\\Users\\bfriesen\\Documents\\visual studio 2013\\Projects\\JsonParserSpike"",
        ""Message"" : ""Deserialize with Sprache.""
    }
}";
            var config = Json.Deserialize(configJson);

            _timer = new Timer(Elapsed, (object)config, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(config.Git.Path)
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

            Console.WriteLine("Press \'q\' to quit.");
            while (Console.Read() != 'q') { }
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
            dynamic config = state;

            // Only one thread is allowed to be processing the file changes
            // at any given time. But threads shouldn't block if they can't
            // obtain the lock - just try again later.

            if (TryGetLock())
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    ProcessFileChanges(config);
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

        private static bool TryTests(string buildOutputPath, IEnumerable<string> testAssemblyFilePaths)
        {
            return TryTests(testAssemblyFilePaths.Select(x => Path.Combine(buildOutputPath, x)).ToArray());
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
            catch
            {
                return false;
            }
        }
    }
}
