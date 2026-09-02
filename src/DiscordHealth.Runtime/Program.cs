using DiscordHealth.Runtime;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDiscordHealth(builder.Configuration);
await builder.Build().RunAsync();
