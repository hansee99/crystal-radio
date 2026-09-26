using RadioPlayer.Web.Components;
using RadioPlayer.Web.Hosting;

// Crystal Radio's web head: a remote control for the player running on this machine (a Raspberry
// Pi, or the dev box). There is NO authentication — it is meant for a home LAN, like most
// network audio appliances. Don't expose the port to the internet.
//
// Listen address comes from the environment, not code: ASPNETCORE_URLS=http://0.0.0.0:5000 on the
// Pi (see deploy/pi), launchSettings.json's http://localhost:5000 in development. Plain HTTP on
// purpose: an appliance on a LAN has no certificate to serve.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One player per process, shared by every browser tab.
builder.Services.AddSingleton<PlayerHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayerHost>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapStatus();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
