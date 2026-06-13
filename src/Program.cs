using CurseForgeProxy;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Debug);

builder.Services.AddSingleton<EnvironmentConfiguration>();
builder.Services.AddSingleton<CurseForgeEndpoints>();

builder.Services
    .AddEgressPool(options =>
    {
        options.AddressMode = Egress.EgressAddressMode.AssignOnDemand;
    })
    .AddHttpClient(name: CurseForgeEndpoints.HttpClientName)
    .UseEgressPool();

var app = builder.Build();

app.Services
    .GetRequiredService<CurseForgeEndpoints>()
    .ConfigureRoutes(app);

app.Run();

public partial class Program;
