using System;

namespace GolemBuild
{
    public static class Logger
    {
        public static event Action<string> OnError;
        public static event Action<string> OnMessage;

        public static void LogError(string message)
        {
            OnError?.Invoke(message);
        }

        //TODO: add some verbosity level
        public static void LogMessage(string message)
        {
            OnMessage?.Invoke(message);
        }
    }
}
