namespace FileWatcherSpike
{
    public delegate bool TryParseFunc<T>(string s, out T value);
}
