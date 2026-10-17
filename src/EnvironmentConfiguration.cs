namespace CurseForgeProxy;

internal sealed class EnvironmentConfiguration
{
    public string? CurseForgeApiKey { get; } = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
}
