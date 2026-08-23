using System;
using Newtonsoft.Json.Linq;
using DV.Signs;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Reports the trackside signs Derail Valley has placed near the player.
    ///
    /// Derail Valley exposes no speed limit anywhere in its API, so the limits
    /// shown elsewhere in this mod are read from the signs themselves. This is
    /// the window onto that: it lists what is actually standing beside the line
    /// and, for each entry on each pole, what sign discovery made of it. When a
    /// limit is not showing on the HUD, this says whether the sign was never
    /// seen, was seen and could not be tied to a track, or was placed on one.
    ///
    /// It reads SignGeneratorData - the component the game itself puts sign
    /// content on, and the same source the speed limit reader uses. It used to
    /// walk every MonoBehaviour in the scene, guess from type and object names
    /// which ones might be signs, and pull their text out by reflecting over
    /// every component and field beneath them. That answered the same question
    /// far less exactly and at a cost that could be felt in the game.
    public static class SignDiscovery
    {
        /// Anything nearer than this to the player, so a scan stays cheap and
        /// returns what is actually around rather than the whole map.
        public const float DefaultRadius = 400f;

        /// Poles reported at most, however wide the radius.
        private const int MaxPoles = 200;

        public static string GetNearbySignsJson(float radius)
        {
            var origin = PlayerOrigin();
            var results = new JArray();

            foreach (var data in UnityEngine.Object.FindObjectsOfType<SignGeneratorData>())
            {
                if (data == null || data.signParameters == null)
                    continue;

                // The raw transform is what identifies a sign to discovery; the
                // shifted one is what can be put on the map, since the world
                // moves under the player over long distances.
                var position = data.transform.position;
                var distance = Vector3.Distance(position, origin);
                if (radius > 0f && distance > radius)
                    continue;

                var entries = new JArray();
                for (var i = 0; i < data.signParameters.Length; i++)
                {
                    var parameters = data.signParameters[i];
                    var placed = SpeedSigns.TryDescribePlacement(position, i, out var placement);
                    entries.Add(new JObject(
                        new JProperty("index", i),
                        new JProperty("type", parameters.type.ToString()),
                        new JProperty("text", parameters.signText ?? ""),
                        new JProperty("placed", placed),
                        new JProperty("placement", placement)));
                }

                var moved = position - WorldMover.currentMove;
                results.Add(new JObject(
                    new JProperty("gameObject", data.gameObject.name),
                    new JProperty("distance", Math.Round(distance, 1)),
                    new JProperty("position",
                        new World.Position(moved.x, moved.z).ToLatLon().ToJson()),
                    new JProperty("entries", entries)));

                if (results.Count >= MaxPoles)
                    break;
            }

            return new JObject(
                new JProperty("radius", radius),
                new JProperty("count", results.Count),
                new JProperty("truncated", results.Count >= MaxPoles),
                new JProperty("speedSignsKnown", SpeedSigns.KnownCount),
                new JProperty("signs", results)).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static Vector3 PlayerOrigin()
        {
            var car = PlayerManager.Car;
            if (car != null)
                return car.transform.position;
            var player = PlayerManager.PlayerTransform;
            return player != null ? player.position : Vector3.zero;
        }
    }
}
