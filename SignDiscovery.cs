using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// Reports the trackside signs present in the loaded world.
    ///
    /// Derail Valley exposes no speed limit anywhere in its API, so the only way
    /// to show the limits it actually places is to read the signs themselves.
    /// This walks the scene rather than guessing from the assemblies, and reads
    /// text through reflection so no TextMeshPro reference is needed.
    public static class SignDiscovery
    {
        /// Anything nearer than this to the player, so a scan stays cheap and
        /// returns what is actually around rather than the whole map.
        public const float DefaultRadius = 400f;

        public static string GetNearbySignsJson(float radius)
        {
            var origin = PlayerOrigin();
            var results = new JArray();

            foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null)
                    continue;
                var typeName = behaviour.GetType().Name;
                if (!LooksLikeSign(typeName, behaviour.gameObject.name))
                    continue;

                var position = behaviour.transform.position;
                if (radius > 0f && Vector3.Distance(position, origin) > radius)
                    continue;

                results.Add(new JObject(
                    new JProperty("component", behaviour.GetType().FullName),
                    new JProperty("gameObject", behaviour.gameObject.name),
                    new JProperty("distance", Math.Round(Vector3.Distance(position, origin), 1)),
                    new JProperty("texts", new JArray(TextsUnder(behaviour.gameObject))),
                    new JProperty("fields", new JArray(NumericFields(behaviour)))));

                if (results.Count >= 200)
                    break;
            }

            return new JObject(
                new JProperty("radius", radius),
                new JProperty("count", results.Count),
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

        private static bool LooksLikeSign(string typeName, string objectName)
        {
            return Contains(typeName, "sign") || Contains(typeName, "speed")
                || Contains(objectName, "sign") || Contains(objectName, "speed")
                || Contains(objectName, "limit");
        }

        private static bool Contains(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// Any text rendered under this object, whatever component carries it.
        /// TextMeshPro and UI text both expose a `text` property, so reflection
        /// covers them without referencing either package.
        private static IEnumerable<string> TextsUnder(GameObject root)
        {
            foreach (var child in root.GetComponentsInChildren<Component>(true))
            {
                if (child == null)
                    continue;
                var property = child.GetType().GetProperty("text");
                if (property == null || property.PropertyType != typeof(string))
                    continue;
                string value;
                try
                {
                    value = property.GetValue(child, null) as string;
                }
                catch
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(value))
                    yield return child.GetType().Name + "=" + value.Trim();
            }
        }

        /// Numeric fields on the component, in case a limit is held as a value
        /// rather than rendered as text.
        private static IEnumerable<string> NumericFields(MonoBehaviour behaviour)
        {
            foreach (var field in behaviour.GetType().GetFields(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance))
            {
                if (field.FieldType != typeof(int) && field.FieldType != typeof(float))
                    continue;
                object value;
                try
                {
                    value = field.GetValue(behaviour);
                }
                catch
                {
                    continue;
                }
                yield return field.Name + "=" + value;
            }
        }
    }
}
