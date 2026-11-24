using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using MiniLeague.Web;
using MiniLeague.Web.Services;
using MiniLeague.Engine.AI;
using MiniLeague.Engine.Simulation;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// MudBlazor
builder.Services.AddMudServices();

// Game engine services
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<TeamRepository>();
builder.Services.AddScoped<SoundService>();
builder.Services.AddScoped<FieldViewPreferences>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<PwaService>();
builder.Services.AddScoped<CastService>();
builder.Services.AddSingleton<Random>(_ => Random.Shared);
builder.Services.AddScoped<IGameSaveStore, GameSaveStore>();
builder.Services.AddScoped<AtBatResolver>();
builder.Services.AddScoped<ComputerManager>();

await builder.Build().RunAsync();
