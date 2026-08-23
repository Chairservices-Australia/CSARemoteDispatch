using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DV.Signs;
using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// The speed limits Derail Valley actually posts, read from the signs it
    /// places rather than derived from track geometry.
    ///
    /// There is no speed limit anywhere in the game's API, but every trackside
    /// sign carries a SignParameters entry naming its type and the text printed
    /// on it, so a SpeedLimit sign states its own value.
    public static class SpeedSigns
    {
        public readonly struct Sign
        {
            public readonly Vector3 position;
            public readonly Vector3 facing;
            public readonly int kph;

            public Sign(Vector3 position, Vector3 facing, int kph)
            {
                this.position = position;
                this.facing = facing;
                this.kph = kph;
            }
        }

        /// Signs stream in and out with the world, so they are accumulated as
        /// they are seen rather than scanned once, keyed by rounded position so
        /// the same sign is not stored twice.
        private static readonly Dictionary<Vector3Int, Sign> known = new Dictionary<Vector3Int, Sign>();
        private static float nextScanTime;

        public const float RescanSeconds = 5f;
        /// How far back a passed sign still governs. Beyond this the limit is
        /// treated as unknown rather than reported from a sign long gone.
        public const float MaxDistanceBehind = 4000f;

        public static int KnownCount => known.Count;

        public static void ScanIfDue()
        {
            if (Time.time < nextScanTime)
                return;
            nextScanTime = Time.time + RescanSeconds;
            Scan();
        }

        public static void Scan()
        {
            foreach (var data in Object.FindObjectsOfType<SignGeneratorData>())
            {
                if (data == null || data.signParameters == null)
                    continue;
                foreach (var parameters in data.signParameters)
                {
                    if (!IsSpeedLimit(parameters.type))
                        continue;
                    if (!TryParseSpeed(parameters.signText, out var kph))
                        continue;

                    var transform = data.transform;
                    var position = transform.position;
                    var key = new Vector3Int(
                        Mathf.RoundToInt(position.x),
                        Mathf.RoundToInt(position.y),
                        Mathf.RoundToInt(position.z));
                    known[key] = new Sign(position, transform.forward, kph);
                }
            }
        }

        private static bool IsSpeedLimit(SignType type) =>
            type == SignType.SpeedLimit
            || type == SignType.SpeedLimitOld
            || type == SignType.SpeedLimitYellow
            || type == SignType.SpeedLimitYellowOld;

        /// Sign text is the limit in tens of km/h on DV's signs ("6" is 60), but
        /// accept a plain speed too in case a sign spells it out.
        private static bool TryParseSpeed(string text, out int kph)
        {
            kph = 0;
            if (string.IsNullOrEmpty(text))
                return false;
            var trimmed = new string(text.Where(char.IsDigit).ToArray());
            if (trimmed.Length == 0)
                return false;
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return false;
            kph = value <= 15 ? value * 10 : value;
            return kph > 0 && kph <= 200;
        }

        /// The limit in force at a position for a train travelling `heading`.
        ///
        /// Signs divide the line into zones: a limit applies from the sign that
        /// posts it until the next one, so the governing sign is the nearest one
        /// already passed. Signs facing the other way belong to the opposite
        /// direction, and signs set well off to the side belong to a parallel
        /// line, so both are skipped - but if that leaves nothing, the nearest
        /// passed sign is used anyway rather than reporting no limit at all.
        public static int? LimitAt(Vector3 position, Vector3 heading)
        {
            var flatHeading = new Vector3(heading.x, 0, heading.z);
            if (flatHeading.sqrMagnitude < 0.0001f || known.Count == 0)
                return null;
            flatHeading = flatHeading.normalized;

            var bestFacing = float.MaxValue;
            int? facingLimit = null;
            var bestAny = float.MaxValue;
            int? anyLimit = null;

            foreach (var sign in known.Values)
            {
                var toTrain = position - sign.position;
                toTrain.y = 0;
                var behind = Vector3.Dot(toTrain, flatHeading);
                if (behind < 0f || behind > MaxDistanceBehind)
                    continue;   // not reached yet, or too far back to still apply

                var lateral = (toTrain - flatHeading * behind).magnitude;
                if (lateral > MaxLateralMeters)
                    continue;   // belongs to a line running alongside this one

                if (behind < bestAny)
                {
                    bestAny = behind;
                    anyLimit = sign.kph;
                }

                // A sign posted for this direction faces the traffic reading it,
                // so its forward points back along the way the train is going.
                var facing = new Vector3(sign.facing.x, 0, sign.facing.z);
                if (facing.sqrMagnitude > 0.0001f
                    && Vector3.Dot(facing.normalized, flatHeading) > 0.3f)
                    continue;

                if (behind < bestFacing)
                {
                    bestFacing = behind;
                    facingLimit = sign.kph;
                }
            }

            return facingLimit ?? anyLimit;
        }

        /// How far to the side a sign may sit and still be read as governing
        /// this line.
        public const float MaxLateralMeters = 40f;
    }
}
