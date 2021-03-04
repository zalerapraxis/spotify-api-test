using System.IO;
using System.Threading.Tasks;
using System;
using System.Net;
using SpotifyAPI.Web.Auth;
using SpotifyAPI.Web.Http;
using SpotifyAPI.Web;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using ImageMagick;
using ImageMagick.Formats;
using Newtonsoft.Json;
using static SpotifyAPI.Web.Scopes;
using Color = System.Drawing.Color;

namespace spotify_api_test
{
    /// <summary>
    ///   This is a basic example how to get user access using the Auth package and a CLI Program
    ///   Your spotify app needs to have http://localhost:5050 as redirect uri whitelisted
    /// </summary>
    public class Program
    {
        private const string CredentialsPath = "credentials.json";
        private static readonly string? clientId = "";
        private static readonly string? clientSecret = "";
        private static EmbedIOAuthServer _server;

        public static async Task<int> Main()
        {
            if (File.Exists(CredentialsPath))
            {
                await Start();
            }
            else
            {
                await StartAuthentication();
            }

            Console.WriteLine("===");
            Console.ReadLine();

            return 0;
        }

        private static async Task Start()
        {
            // find the lights
            //var findBulbs = Task.Run(() => YeelightHelper.FindDevices());
            //findBulbs.Wait();

            // connect to the lights
            //Task.Run(() => YeelightHelper.ConnectToBulbs());
            //YeelightHelper.ConnectToBulbs();

            await YeelightHelper.InitializeYeelights();

            // do all that cool Spotify authentication shit
            var json = await File.ReadAllTextAsync(CredentialsPath);
            var token = JsonConvert.DeserializeObject<AuthorizationCodeTokenResponse>(json);

            var authenticator = new AuthorizationCodeAuthenticator(clientId!, clientSecret!, token);
            authenticator.TokenRefreshed += (sender, token) => File.WriteAllText(CredentialsPath, JsonConvert.SerializeObject(token));

            var config = SpotifyClientConfig.CreateDefault()
              .WithAuthenticator(authenticator);

            // build spotify client
            var spotify = new SpotifyClient(config);

            // get user account data
            var me = await spotify.UserProfile.Current();
            Console.WriteLine($"[Spotify] Authenticated as {me.DisplayName} ({me.Id}).");

            // flash the lights green & back to default to show we're alive
            // YeelightHelper.bulbs.SetRGBColor(0, 255, 0, 250);
            await Task.Delay(1000);
            //YeelightHelper.bulbs.SetColorTemperature(2700, 250);


            string oldTrackId = ""; // used to track if we're playing a different track than last loop
            int loopDelay = 2000; // don't set below 1000ms.
            while (true)
            {
                // for measuring api times
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                // get info on playback status
                var currentTrack = await spotify.Player.GetCurrentPlayback(new PlayerCurrentPlaybackRequest());

                // check if we're playing something, or if we pulled null data somehow (API failure?)
                if (currentTrack == null || currentTrack.Item == null || currentTrack.IsPlaying == false)
                {
                    await Task.Delay(loopDelay);
                    continue;
                }
                
                // grab the track details
                var currentTrackDetails = (FullTrack) currentTrack.Item;

                // check if it's different from what we saw last loop - if so, wait and try the loop again
                if (currentTrackDetails.Id == oldTrackId)
                {
                    await Task.Delay(loopDelay);
                    continue;
                }

                // download the album art to memory
                var albumArtUrl = currentTrackDetails.Album.Images[0].Url;
                WebRequest request = WebRequest.Create(albumArtUrl);
                WebResponse response = request.GetResponse();
                Stream responseStream = response.GetResponseStream();

                // convert album art to color - color should be average color of image after some adjustments
                Color albumColor = new Color();
                using (MagickImage image = new MagickImage(responseStream, MagickFormat.Jpg))
                {
                    // debug - output image before doing anything at all
                    var debugOutputPreProcessing = new FileInfo("debug-output-album-pre-processing.jpg");
                    image.Write(debugOutputPreProcessing);

                    // set white & black to transparent, to make sure they don't mess with the vibrancy of the image
                    image.ColorFuzz = (Percentage) 15; // shades within 15% of pure white/black will count as pure
                    image.Opaque(new MagickColor(MagickColors.White), new MagickColor(MagickColors.Transparent));
                    image.Opaque(new MagickColor(MagickColors.Black), new MagickColor(MagickColors.Transparent));

                    // flatten image down to a few colors
                    image.Quantize(new QuantizeSettings { Colors = 5 });

                    // debug - output image after quantizing
                    var debugOutputQuantized = new FileInfo("debug-output-album-quantized.jpg");
                    image.Write(debugOutputQuantized);

                    // massively boost saturation so the lights actually have color to them
                    image.Modulate((Percentage)100, (Percentage)1000, (Percentage)100);

                    // resize to 1px, as the built-in converter will average out all the remaining colors into one
                    image.Resize(1, 1);
                    
                    // get color rgb values and set to albumColor
                    var colorKey = image.Histogram().FirstOrDefault().Key;
                    albumColor = Color.FromArgb(255, colorKey.R, colorKey.G, colorKey.B);

                    // debug - output after all post processing. should return a 100x100px image of one solid color.
                    image.Resize(100, 100);
                    var debugOutputDone = new FileInfo("debug-output-done.jpg");
                    image.Write(debugOutputDone);

                    // get rid of any junk we don't need anymore
                    image.Dispose();
                    responseStream.Dispose();
                }

                // set the light color to the album art's average color
                YeelightHelper.bulbs.SetRGBColor(albumColor.R, albumColor.G, albumColor.B, 250);


                stopwatch.Stop();
                oldTrackId = currentTrackDetails.Id;

                Console.WriteLine($"[Spotify] {currentTrackDetails.Artists[0].Name} - {currentTrackDetails.Name}" +
                                  $" | {albumColor.R} {albumColor.G} {albumColor.B}" +
                                  $" | performed in {stopwatch.ElapsedMilliseconds}ms");

                await Task.Delay(loopDelay);
            }
        }

        private static async Task StartAuthentication()
        {
            _server = new EmbedIOAuthServer(new Uri("http://localhost:5050/callback"), 5050);

            await _server.Start();
            _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;

            var request = new LoginRequest(_server.BaseUri, clientId!, LoginRequest.ResponseType.Code)
            {
                Scope = new List<string> { UserReadEmail, UserReadPrivate, PlaylistReadPrivate, PlaylistReadCollaborative, UserLibraryRead, UserLibraryModify, UserReadCurrentlyPlaying, UserReadPlaybackPosition, UserReadPlaybackState }
            };

            Uri uri = request.ToUri();
            try
            {
                BrowserUtil.Open(uri);
            }
            catch (Exception)
            {
                Console.WriteLine("[AUTH] Unable to open URL, manually open: {0}", uri);
            }
        }

        private static async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            await _server.Stop();
            AuthorizationCodeTokenResponse token = await new OAuthClient().RequestToken(
              new AuthorizationCodeTokenRequest(clientId!, clientSecret!, response.Code, _server.BaseUri)
            );

            await File.WriteAllTextAsync(CredentialsPath, JsonConvert.SerializeObject(token));

            // get rid of the web server
            _server.Dispose();

            await Start();
        }


    }
}
