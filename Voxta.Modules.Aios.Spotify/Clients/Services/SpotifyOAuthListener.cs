using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Voxta.Modules.Aios.Spotify.Configuration;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

/// <summary>Lives only while awaiting OAuth. No dependency on Voxta's private MVC routing.</summary>
internal sealed class SpotifyOAuthListener(WebApplication app) : IAsyncDisposable
{
    public static async Task<SpotifyOAuthListener> StartAsync(Uri redirectUri, ISpotifyAuthCallbackManager callbacks, CancellationToken token)
    {
        if (SpotifyRedirectUri.GetError(redirectUri) is { } error) throw new InvalidOperationException(error);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(SpotifyOAuthListener).Assembly.FullName });
        // Never log authorization query strings or consume the host's configuration/ports.
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Parse(redirectUri.Host.Trim('[', ']')), redirectUri.Port));
        var app = builder.Build();
        app.MapGet(SpotifyRedirectUri.CallbackPath, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var state = context.Request.Query["state"].ToString();
            var code = context.Request.Query["code"].ToString();
            var denied = context.Request.Query.ContainsKey("error");
            if (string.IsNullOrEmpty(state) || (!denied && string.IsNullOrEmpty(code)))
                return Results.Text("Missing Spotify authorization parameters.", statusCode: 400);
            var accepted = denied ? callbacks.Reject(state) : callbacks.Callback(code, state);
            if (accepted != SpotifyAuthCallbackResult.Accepted)
                return Results.Text("This Spotify authorization request is no longer active. Return to Voxta and start authorization again.", statusCode: 410);
            return Results.Content("""
                <!doctype html><html><head><meta charset="utf-8"><title>Spotify authorization</title>
                <style>body{background:#121212;color:white;font:18px system-ui;display:grid;place-items:center;min-height:95vh;text-align:center}</style></head>
                <body><main><h1>Spotify authorization received</h1><p>Return to Voxta to see the result. You can close this tab.</p></main></body></html>
                """, "text/html");
        });
        try { await app.StartAsync(token); }
        catch { await app.DisposeAsync(); throw; }
        return new(app);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); }
    }
}
