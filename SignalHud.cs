using UnityEngine;

namespace DvMod.RemoteDispatch
{
    /// In-game overlay in the top right: a circular speed sign and a
    /// three-position colour light showing the state of the line ahead.
    ///
    /// Drawn with IMGUI and textures generated at runtime. The mod ships no
    /// asset bundle, and a handful of small circles are cheaper to draw than to
    /// package.
    public class SignalHud : MonoBehaviour
    {
        private const int SignSize = 96;
        private const int LampSize = 34;
        private const float MarginX = 24f;
        private const float MarginY = 24f;
        private const float Gap = 12f;

        private static Texture2D? speedSignTexture;
        private static Texture2D? signalBodyTexture;
        private static readonly Texture2D?[] lampTextures = new Texture2D?[6];

        private static GUIStyle? speedTextStyle;
        private static Font? signFont;

        /// Speed signs are set in DIN 1451, the road-sign face. Bahnschrift is
        /// Microsoft's cut of it and ships with Windows; the rest are condensed
        /// grotesques in descending order of similarity, ending at whatever the
        /// default is so the sign always renders.
        private static readonly string[] SignFontCandidates =
        {
            "Bahnschrift SemiBold",
            "Bahnschrift",
            "Arial Narrow Bold",
            "Arial Narrow",
            "Franklin Gothic Medium Cond",
            "Arial Black",
            "Impact",
        };

        private float nextReadTime;
        private Signalling.Reading reading;

        private static GameObject? rootObject;

        public static void Create()
        {
            if (rootObject != null)
                return;
            rootObject = new GameObject("CSARemoteDispatch_SignalHud");
            DontDestroyOnLoad(rootObject);
            rootObject.AddComponent<SignalHud>();
        }

        public static void Destroy()
        {
            if (rootObject == null)
                return;
            Object.Destroy(rootObject);
            rootObject = null;
        }

        public void Update()
        {
            // Reading the line walks track and scans every car, so it runs a few
            // times a second rather than every frame.
            if (Time.time < nextReadTime)
                return;
            nextReadTime = Time.time + 0.25f;
            reading = Signalling.ReadForPlayer();
        }

        public void OnGUI()
        {
            if (!Main.settings.showSignalHud)
                return;
            if (reading.aspect == Aspect.Unknown && reading.speedLimitKph == 0)
                return;

            EnsureTextures();

            var signalWidth = LampSize + 12f;
            var signalHeight = LampSize * 3 + 24f;
            var signalX = Screen.width - MarginX - signalWidth;
            var signalY = MarginY;

            var signX = signalX - Gap - SignSize;
            var signY = MarginY;

            DrawSpeedSign(new Rect(signX, signY, SignSize, SignSize), reading.speedLimitKph);
            DrawSignal(new Rect(signalX, signalY, signalWidth, signalHeight), reading.aspect);
        }

        private void DrawSpeedSign(Rect rect, int kph)
        {
            if (speedSignTexture != null)
                GUI.DrawTexture(rect, speedSignTexture);

            var text = kph.ToString();
            // Three digits have to sit inside the same ring as two, so the face
            // is set narrower rather than letting the number overrun the border.
            var fontSize = Mathf.RoundToInt(SignSize * (text.Length >= 3 ? 0.36f : 0.46f));
            var style = SpeedTextStyle(fontSize);

            // Centred by measuring the glyphs rather than by anchoring: label
            // styles carry padding and the line box is taller than the digits,
            // both of which push an anchored number off centre.
            var content = new GUIContent(text);
            var size = style.CalcSize(content);
            var textRect = new Rect(
                rect.x + (rect.width - size.x) / 2f,
                rect.y + (rect.height - size.y) / 2f,
                size.x,
                size.y);
            GUI.Label(textRect, content, style);
        }

        private static GUIStyle SpeedTextStyle(int fontSize)
        {
            if (speedTextStyle == null)
            {
                speedTextStyle = new GUIStyle
                {
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    border = new RectOffset(0, 0, 0, 0),
                    contentOffset = Vector2.zero,
                    wordWrap = false,
                    richText = false,
                };
                speedTextStyle.normal.background = null;
            }

            if (signFont == null)
                signFont = LoadSignFont();
            speedTextStyle.font = signFont;
            speedTextStyle.fontStyle = signFont == null ? FontStyle.Bold : FontStyle.Normal;
            speedTextStyle.fontSize = fontSize;
            speedTextStyle.normal.textColor = Color.black;
            return speedTextStyle;
        }

        private static Font? LoadSignFont()
        {
            string[] installed;
            try
            {
                installed = Font.GetOSInstalledFontNames() ?? new string[0];
            }
            catch
            {
                return null;
            }

            foreach (var wanted in SignFontCandidates)
            {
                foreach (var name in installed)
                {
                    if (!string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Size is set per draw; this only selects the face.
                    var font = Font.CreateDynamicFontFromOSFont(name, 32);
                    if (font != null)
                        return font;
                }
            }
            return null;
        }

        private void DrawSignal(Rect rect, Aspect aspect)
        {
            if (signalBodyTexture != null)
                GUI.DrawTexture(rect, signalBodyTexture);

            // Green at the top, amber in the middle, red at the bottom.
            var lit = new[]
            {
                aspect == Aspect.Clear,
                aspect == Aspect.Caution,
                aspect == Aspect.Stop,
            };

            for (var i = 0; i < 3; i++)
            {
                var texture = lampTextures[i * 2 + (lit[i] ? 0 : 1)];
                if (texture == null)
                    continue;
                var lampRect = new Rect(
                    rect.x + (rect.width - LampSize) / 2f,
                    rect.y + 6f + i * (LampSize + 3f),
                    LampSize,
                    LampSize);
                GUI.DrawTexture(lampRect, texture);
            }
        }

        private static void EnsureTextures()
        {
            if (speedSignTexture != null)
                return;

            speedSignTexture = CreateCircle(SignSize, Color.white, new Color(0.78f, 0.05f, 0.05f), 0.18f);
            signalBodyTexture = CreateRoundedPanel(LampSize + 12, LampSize * 3 + 24, new Color(0.09f, 0.09f, 0.1f, 0.94f));

            var green = new Color(0.15f, 0.85f, 0.25f);
            var amber = new Color(1f, 0.65f, 0.05f);
            var red = new Color(0.92f, 0.15f, 0.12f);
            var colors = new[] { green, amber, red };

            for (var i = 0; i < 3; i++)
            {
                lampTextures[i * 2] = CreateCircle(LampSize, colors[i], new Color(0, 0, 0, 0.65f), 0.10f);
                // Unlit lamps stay visible but dark, as a real head reads.
                var dark = colors[i] * 0.16f;
                dark.a = 1f;
                lampTextures[i * 2 + 1] = CreateCircle(LampSize, dark, new Color(0, 0, 0, 0.65f), 0.10f);
            }
        }

        /// Filled circle with a border ring, anti-aliased at both edges.
        private static Texture2D CreateCircle(int size, Color fill, Color border, float borderFraction)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var center = (size - 1) / 2f;
            var outer = size / 2f - 1f;
            var inner = outer * (1f - borderFraction);
            var pixels = new Color[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                    Color color;
                    if (distance > outer)
                    {
                        color = new Color(0, 0, 0, 0);
                    }
                    else if (distance > inner)
                    {
                        color = border;
                        // Feather the outside edge.
                        color.a *= Mathf.Clamp01(outer - distance);
                    }
                    else
                    {
                        // Feather the border/fill boundary.
                        color = Color.Lerp(fill, border, Mathf.Clamp01(distance - (inner - 1f)));
                    }
                    pixels[y * size + x] = color;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private static Texture2D CreateRoundedPanel(int width, int height, Color color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            const float radius = 6f;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var dx = Mathf.Max(radius - x, x - (width - 1 - radius), 0f);
                    var dy = Mathf.Max(radius - y, y - (height - 1 - radius), 0f);
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = color.a * Mathf.Clamp01(radius - distance + 1f);
                    pixels[y * width + x] = new Color(color.r, color.g, color.b, distance > radius ? 0f : alpha);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }
    }
}
