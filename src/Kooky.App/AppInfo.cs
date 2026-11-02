namespace Kooky.App;
internal static class AppInfo
{
    public static readonly string Version=typeof(Program).Assembly.GetName().Version?.ToString(3)??"0.0.0";
}
