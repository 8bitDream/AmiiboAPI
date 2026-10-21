namespace AmiiboGameList;

internal class Debugger
{
    public static DebugLevel CurrentDebugLevel;
    public enum DebugLevel
    {
        Verbose,
        Info,
        Warn,
        Error
    }

    public enum ReturnType
    {
        Success = 2,
        SuccessWithErrors = 1,
        UnknownError = -1,
        InternetError = -2,
        DatabaseLoadingError = -3
    }

    public static void Log(string Message) => Log(Message, DebugLevel.Info);

    public static void Log(string Message, DebugLevel Severity)
    {
        if (Severity >= CurrentDebugLevel)
        {
            switch (Severity)
            {
                case DebugLevel.Verbose:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    break;
                case DebugLevel.Info:
                    Console.ForegroundColor = ConsoleColor.White;
                    break;
                case DebugLevel.Warn:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case DebugLevel.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                default:
                    break;
            }

            Console.WriteLine(Message);
        }
    }
}
