using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    public class HttpServer : MonoBehaviour
    {
        private static GameObject? rootObject;
        private readonly HttpListener listener = new HttpListener();

        public async void Start()
        {
            if (!listener.IsListening)
            {
                listener.Prefixes.Add($"http://*:{Main.settings.serverPort}/");
                listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous | AuthenticationSchemes.Basic;
                listener.Realm = "DV Remote Dispatch";
                Main.DebugLog(() => $"Starting HTTP server on port {Main.settings.serverPort}");
                try
                {
                    listener.Start();
                }
                catch (Exception e)
                {
                    Main.mod?.Logger.Error(
                        $"Could not start HTTP server on port {Main.settings.serverPort}: {e.Message}");
                    return;
                }
            }

            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync().ConfigureAwait(true);
                    if (CheckAuthentication(context))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await HandleRequest(context).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Main.DebugLog(() => $"Exception while handling HTTP request ({context.Request.Url}): {e}");
                            }
                        });
                    }
                    else
                    {
                        context.Response.Headers.Add("WWW-Authenticate", "Basic");
                        RenderEmpty(context, 401);
                    }
                }
                catch (ObjectDisposedException e) when (e.ObjectName == "listener")
                {
                    // ignore when OnDestroy() is called to shutdown the server
                }
                catch (HttpListenerException) when (!listener.IsListening)
                {
                    // Stop() interrupts a pending GetContextAsync call.
                }
                catch (Exception e)
                {
                    Main.mod?.Logger.Error($"HTTP listener stopped unexpectedly: {e}");
                    break;
                }
            }
        }

        public void OnDestroy()
        {
            if (listener.IsListening)
            {
                Main.DebugLog(() => "Stopping HTTP server");
                listener.Stop();
                listener.Prefixes.Clear();
            }
        }

        private static bool CheckAuthentication(HttpListenerContext context)
        {
            string serverPassword = Main.settings.serverPassword;
            return context.User?.Identity is HttpListenerBasicIdentity identity && (string.IsNullOrEmpty(serverPassword) || identity.Password == serverPassword);
        }

        private static async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            if (request.Url.Segments.Length < 2)
            {
                context.Response.ContentType = ContentTypes.Html;
                RenderResource(context, "index.html");
                return;
            }

            switch (request.Url.Segments[1].TrimEnd('/'))
            {
            case "car":
                await HandleCarRequest(context).ConfigureAwait(false);
                break;
            case "job":
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(
                    JobData.GetAllJobDataJson).ConfigureAwait(false));
                break;
            case "junction":
                await HandleJunctionRequest(context).ConfigureAwait(false);
                break;
            case "junctionState":
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(
                    Junctions.GetJunctionStateJSON).ConfigureAwait(false));
                break;
            case "player":
                var playerJson = await Updater.RunOnMainThread(
                    PlayerData.GetPlayerDataJson).ConfigureAwait(false);
                if (playerJson != null)
                    Render200(context, ContentTypes.Json, playerJson);
                else
                    RenderError(context, 500, "Player position is not available yet.");
                break;
            case "res":
                RenderResource(context);
                break;
            case "signs":
            {
                var radiusText = request.QueryString.Get("radius");
                var radius = float.TryParse(radiusText, out var parsed) ? parsed : SignDiscovery.DefaultRadius;
                var json = await Updater.RunOnMainThread(
                    () => SignDiscovery.GetNearbySignsJson(radius)).ConfigureAwait(false);
                Render200(context, ContentTypes.Json, json);
                break;
            }
            case "currentTrain":
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(() =>
                    CurrentTrain.GetCurrentTrainJson().ToString(Formatting.None)).ConfigureAwait(false));
                break;
            case "route":
                await HandleRouteRequest(context).ConfigureAwait(false);
                break;
            case "station":
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(
                    Stations.GetStationJSON).ConfigureAwait(false));
                break;
            case "track":
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(() =>
                    RailTracks.GetTrackPointJSON().GetAwaiter().GetResult()).ConfigureAwait(false));
                break;
            case "trainset":
                HandleTrainsetRequest(context);
                break;
            case "updates":
                await HandleUpdatesRequest(context).ConfigureAwait(false);
                break;
            default:
                RenderEmpty(context, 404);
                break;
            }
        }

        private static async Task HandleCarRequest(HttpListenerContext context)
        {
            var segments = context.Request.Url.Segments;
            if (segments.Length == 2 && context.Request.HttpMethod == "GET")
            {
                var allCarDataJson = CarData.GetAllCarDataJson();
                Render200(context, allCarDataJson);
                return;
            }

            if (segments.Length == 3 && context.Request.HttpMethod == "GET")
            {
                var carGuid = segments[2].TrimEnd('/');
                var carDataJson = CarData.GetCarGuidDataJson(carGuid);
                if (carDataJson == null)
                    RenderError(context, 404, "No car with GUID \"" + carGuid + "\".");
                else
                    Render200(context, carDataJson);
                return;
            }

            if (segments.Length == 4 && segments[3] == "control" && context.Request.HttpMethod == "POST")
            {
                var carGuid = segments[2].TrimEnd('/');
                var controller = LocoControl.GetLocoController(carGuid);
                if (controller == null)
                {
                    RenderEmpty(context, 404);
                    return;
                }
                if (!Main.settings.permissions.HasLocoControlPermission(context.User.Identity.Name))
                {
                    RenderEmpty(context, 403);
                    return;
                }
                var success = await Updater.RunOnMainThread(() =>
                    LocoControl.RunCommand(controller, context.Request.QueryString)
                ).ConfigureAwait(false);
                RenderEmpty(context, success ? 204 : 400);
                return;
            }
            RenderEmpty(context, 404);
        }

        private static async Task HandleUpdatesRequest(HttpListenerContext context)
        {
            if (context.Request.Url.Segments.Length < 3)
            {
                RenderEmpty(context, 404);
                return;
            }

            var username = context.User?.Identity?.Name ?? "";
            var sessionId = context.Request.Url.Segments[2];
            Render200(context, ContentTypes.Json, await Sessions.GetUpdates(username, sessionId).ConfigureAwait(false));
        }

        private static bool IsValidJunctionId(int junctionId)
        {
            return junctionId >= 0 && junctionId < RailTrackRegistry.Instance.OrderedJunctions.Length;
        }

        /// GET  /route                       list active routes
        /// POST /route/{trainsetId}/{trackId} plan and set a route
        /// POST /route/{routeId}/clear        release a route and its junctions
        private static async Task HandleRouteRequest(HttpListenerContext context)
        {
            var url = context.Request.Url;
            var segments = url.Segments;

            if (segments.Length == 2 && context.Request.HttpMethod == "GET")
            {
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(
                    Routing.AllRoutesJson).ConfigureAwait(false));
                return;
            }

            // Setting a route throws switches, so it needs the same permission
            // as toggling junctions by hand.
            if (!Main.settings.permissions.HasJunctionPermission(context.User.Identity.Name))
            {
                RenderError(context, 403, "No junction permission. Enable it for your user "
                    + "in the CSA Remote Dispatch settings in Unity Mod Manager.");
                return;
            }

            if (segments.Length == 4 && context.Request.HttpMethod == "POST")
            {
                var first = segments[2].TrimEnd('/');
                var second = Uri.UnescapeDataString(segments[3].TrimEnd('/'));

                if (second == "clear")
                {
                    var routesJson = await Updater.RunOnMainThread(() =>
                    {
                        Routing.ClearRoute(first);
                        return Routing.AllRoutesJson();
                    }).ConfigureAwait(false);
                    Render200(context, ContentTypes.Json, routesJson);
                    return;
                }

                if (!int.TryParse(first, out var trainsetId))
                {
                    RenderError(context, 404, "\"" + first + "\" is not a train ID.");
                    return;
                }

                string? failure = null;
                var json = await Updater.RunOnMainThread(() =>
                {
                    var trainset = Trainset.allSets.Find(set => set.id == trainsetId);
                    if (trainset == null)
                    {
                        // The page routes by the consist id it last saw, and
                        // coupling or uncoupling builds a new one, so a stale
                        // page can ask for a train that no longer exists.
                        failure = "Train " + trainsetId + " no longer exists - it was probably"
                            + " coupled or uncoupled. Reload the page and pick it again.";
                        return null;
                    }
                    var destination = FindRailTrackById(second);
                    if (destination == null)
                    {
                        failure = "No track called \"" + second + "\".";
                        return null;
                    }
                    return Routing.ToJson(Routing.SetRoute(trainset, destination, second));
                }).ConfigureAwait(false);

                if (json == null)
                {
                    RenderError(context, 404, failure ?? "Could not set a route.");
                    return;
                }
                Render200(context, ContentTypes.Json, json.ToString(Formatting.None));
                return;
            }

            RenderError(context, 404, "Unknown route request.");
        }

        /// Accepts either form of track ID: the canonical "GF-D-05-I" or the
        /// shorter "GF-D5I" the game prints on jobs and signage.
        private static RailTrack? FindRailTrackById(string trackId)
        {
            foreach (var track in Component.FindObjectsOfType<RailTrack>())
            {
                var logicTrack = track == null ? null : track.LogicTrack();
                if (logicTrack == null)
                    continue;
                if (logicTrack.ID.FullID == trackId || logicTrack.ID.FullDisplayID == trackId)
                    return track;
            }
            return null;
        }

        private static async Task HandleJunctionRequest(HttpListenerContext context)
        {
            var url = context.Request.Url;
            switch (url.Segments.Length)
            {
            case 2:
                Render200(context, ContentTypes.Json, await Updater.RunOnMainThread(
                    Junctions.GetJunctionPointJSON).ConfigureAwait(false));
                break;
            case 4:
                var junctionIdString = url.Segments[2].TrimEnd('/');
                if (int.TryParse(junctionIdString, out var junctionId) && url.Segments[3] == "toggle" && IsValidJunctionId(junctionId))
                {
                    if (!Main.settings.permissions.HasJunctionPermission(context.User.Identity.Name))
                    {
                        RenderError(context, 403, "No junction permission. Enable it for your user "
                            + "in the CSA Remote Dispatch settings in Unity Mod Manager.");
                        return;
                    }
                    var newSelectedBranch = await Updater.RunOnMainThread(() =>
                    {
                        Main.DebugLog(() => $"Toggling J-{junctionId}.");
                        var junction = RailTrackRegistry.Instance.OrderedJunctions[junctionId];
                        junction.Switch(Junction.SwitchMode.REGULAR);
                        return junction.selectedBranch;
                    }).ConfigureAwait(false);
                    Render200(context, new JValue(newSelectedBranch));
                    return;
                }
                RenderEmpty(context, 404);
                break;
            default:
                RenderEmpty(context, 404);
                break;
            }
        }

        public static void HandleTrainsetRequest(HttpListenerContext context)
        {
            var request = context.Request;
            if (request.Url.Segments.Length < 3)
            {
                RenderEmpty(context, 404);
                return;
            }
            var trainsetIdText = request.Url.Segments[2].TrimEnd('/');
            if (!int.TryParse(trainsetIdText, out var trainsetId))
            {
                RenderEmpty(context, 404);
                return;
            }
            Render200(context, CarData.GetTrainsetDataJson(trainsetId));
        }

        public static void Create()
        {
            if (rootObject == null)
            {
                rootObject = new GameObject();
                GameObject.DontDestroyOnLoad(rootObject);
                rootObject.AddComponent<HttpServer>();
            }
        }

        public static void Destroy()
        {
            if (rootObject == null)
                return;
            // ensure server shuts down immediately, not at the end of the frame
            DestroyImmediate(rootObject);
            rootObject = null;
        }

        private static void RenderResource(HttpListenerContext context)
        {
            if (context.Request.Url.Segments.Length < 3)
            {
                RenderEmpty(context, 404);
                return;
            }
            var resourceName = context.Request.Url.Segments[2];
            var extension = Path.GetExtension(resourceName);
            context.Response.ContentType = ContentTypes.ForExtension(extension);
            RenderResource(context, resourceName);
        }

        private static void RenderResource(HttpListenerContext context, string resourceName)
        {
            var assembly = typeof(HttpServer).Assembly;
            using var stream = assembly.GetManifestResourceStream(typeof(HttpServer), resourceName);
            if (stream == null)
            {
                RenderEmpty(context, 404);
            }
            else
            {
                stream.CopyTo(context.Response.OutputStream);
                context.Response.Close();
            }
        }

        private static class ContentTypes
        {
            public const string Css = "text/css";
            public const string Html = "text/html; charset=UTF-8";
            public const string Json = "application/json";
            public const string Javascript = "application/javascript";
            public const string Png = "image/png";
            public const string Svg = "image/svg+xml";

            public static string ForExtension(string extension)
            {
                return extension switch
                {
                    ".css" => Css,
                    ".js" => Javascript,
                    ".json" => Json,
                    ".png" => Png,
                    ".svg" => Svg,
                    _ => "",
                };
            }
        }

        private static void Render200(HttpListenerContext context, JToken json)
        {
            Render200(context, ContentTypes.Json, JsonConvert.SerializeObject(json));
        }

        /// A failure carrying a JSON body, so the page has something to show the
        /// user. An empty error response is indistinguishable from a truncated
        /// one once the browser tries to parse it, and reads back as
        /// "unexpected end of data" rather than as whatever actually went wrong.
        private static void RenderError(HttpListenerContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            Render200(context, ContentTypes.Json,
                new JObject(new JProperty("error", message)).ToString(Formatting.None));
        }

        private static void Render200(HttpListenerContext context, string contentType, string s)
        {
            context.Response.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > 128 && (context.Request.Headers.GetValues("Accept-Encoding")?.Contains("gzip") ?? false))
            {
                context.Response.Headers.Add("Content-Encoding", "gzip");
                var mem = new MemoryStream(bytes);
                using var gzip = new GZipStream(context.Response.OutputStream, CompressionMode.Compress);
                mem.CopyTo(gzip);
            }
            else
            {
                context.Response.Close(bytes, false);
            }
        }

        private static void RenderEmpty(HttpListenerContext context, int statusCode)
        {
            context.Response.StatusCode = statusCode;
            context.Response.Close();
        }
    }
}
