using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Voxta.Modules.Aios.Spotify.Clients.Services;

namespace Voxta.Modules.Aios.Spotify.Controllers;

[Route("extensions/spotify")]
[Route("api/extensions/spotify")]
public class SpotifyController : Controller
{
    [HttpGet("oauth2/callback")]
    [AllowAnonymous]
    public IActionResult OAuthCallback(
        [FromQuery] string code,
        [FromQuery] string state,
        [FromServices] ISpotifyAuthCallbackManager spotifyAuthCallbackManager
        )
    {
        if (string.IsNullOrEmpty(code))
            return BadRequest("Missing code");
        if (string.IsNullOrEmpty(state))
            return BadRequest("Missing state");

        var callbackResult = spotifyAuthCallbackManager.Callback(code, state);
        if (callbackResult != SpotifyAuthCallbackResult.Accepted)
        {
            return StatusCode(
                StatusCodes.Status410Gone,
                "This Spotify authorization request is no longer active. Return to Voxta and start authorization again."
            );
        }
        
        return Content(
            // language=html
            """
            <html>
            <head>
                <style>
                    body {
                        background-color: #121212;
                        color: #ffffff;
                        font-family: Arial, sans-serif;
                        height: 100vh;
                        margin: 0;
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        text-align: center;
                    }
                </style>
            </head>
            <body>
                <div>
                    Authentication successful! You can close this tab.
                </div>
            </body>
            </html>
            """,
            "text/html"
        );
    }
}
