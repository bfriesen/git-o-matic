namespace FileWatcherSpike
{
    class Config
    {
        public MSBuild MSBuild { get; set; }
        public string[] TestAssemblyFileNames { get; set; }
        public Git Git { get; set; }
    }
}