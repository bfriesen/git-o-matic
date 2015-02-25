using System.Collections.Generic;

namespace FileWatcherSpike
{
    public class MSBuildOptions
    {
        public string Configuration { get; set; }
        public string Platform { get; set; }
        public string OutputPath { get; set; }

        public IDictionary<string, string> ToDictionary()
        {
            return new Dictionary<string, string>
            {
                { "Configuration", Configuration },
                { "Platform", Platform },
                { "OutputPath", OutputPath }
            };
        }
    }
}