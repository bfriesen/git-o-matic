﻿namespace FileWatcherSpike
{
    public delegate bool TryParseFunc<T>(string input, out T value);
}
