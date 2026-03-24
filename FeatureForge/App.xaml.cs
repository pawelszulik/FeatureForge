using System.Windows;
using FeatureForge.Services;
using FeatureForge.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureForge;

public partial class App : Application
{
    private IServiceProvider _services = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddUserSecrets<App>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ProfileLoaderService>();
        services.AddSingleton<LlmService>();
        services.AddSingleton<TfsService>();
        services.AddSingleton<ProjectContextService>();
        services.AddSingleton<ExportService>(_ => new ExportService(
            System.IO.Path.Combine(AppContext.BaseDirectory, "output")
        ));
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        _services = services.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();
    }
}
