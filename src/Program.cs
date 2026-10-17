using CurseForgeProxy;

using Egress;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

ILoggingBuilder logging = builder.Logging
    .SetMinimumLevel(LogLevel.Warning)
    .AddFilter(nameof(CurseForgeProxy), LogLevel.Debug)
    .AddFilter(nameof(Egress), LogLevel.Debug);

IServiceCollection services = logging.Services
    .AddEgressPool(static options => options.AddressMode = EgressAddressMode.AssignOnDemand)
    .AddHttpClient(name: CurseForgeEndpoints.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(
        static serviceProvider =>
        {
            SocketsHttpHandler handler = serviceProvider.GetRequiredService<EgressPool>().CreateHttpMessageHandler();
            handler.AllowAutoRedirect = false;

            return handler;
        }
    )
    .Services;

services.Add(ServiceDescriptor.Singleton<EnvironmentConfiguration, EnvironmentConfiguration>());
services.Add(ServiceDescriptor.Singleton<CurseForgeEndpoints, CurseForgeEndpoints>());

WebApplication app = builder.Build();

app.Services.GetRequiredService<CurseForgeEndpoints>().ConfigurePipeline(app);
app.Run();
