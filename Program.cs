using EntraOidcScimLab.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<LabConfig>();
        services.AddSingleton<LabStorage>();
        services.AddSingleton<CookieSigner>();
        services.AddSingleton<OidcService>();
    })
    .Build();

host.Run();
