using System.Linq;
using UnityModManagerNet;
using UnityEngine;
using System.Collections.Generic;
using System;

namespace DvMod.RemoteDispatch
{
    public class Settings : UnityModManager.ModSettings
    {
        public int serverPort = 7245;
        public string serverPassword = "";
        public Permissions permissions = new Permissions();
        /// In-game speed sign and signal overlay, top right.
        public bool showSignalHud = true;
        public KeyCode signalHudHotkey = KeyCode.F8;

        public bool showUndiscoveredLocomotives = false;
        public bool enableLogging = false;

        /// A picture drawn under the railway on the map, and the world metres
        /// its corners sit at. See MapOverlay for why this is a picture you
        /// supply rather than one the mod can draw for you.
        public bool showMapImage = true;
        public string mapImagePath = "";
        public float mapImageOpacity = 0.75f;
        public float mapImageMinX;
        public float mapImageMinZ;
        public float mapImageMaxX = 16384f;
        public float mapImageMaxZ = 16384f;

        public readonly string? version = Main.mod?.Info.Version;

        const char EnDash = '\u2013';
        private string uncommittedPort = "initial";
        private string uncommittedMinX = "initial";
        private string uncommittedMaxX = "initial";
        private string uncommittedMinZ = "initial";
        private string uncommittedMaxZ = "initial";
        private string message = "";
        private bool capturingSignalHudHotkey;

        public bool IsCapturingSignalHudHotkey => capturingSignalHudHotkey;

        /// A number the user can type freely into - emptying it, or leaving a
        /// lone minus sign part way through - without the value jumping about
        /// underneath them. Only a reading that parses is taken.
        private static float NumberField(string label, float value, ref string uncommitted)
        {
            if (uncommitted == "initial")
                uncommitted = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            GUILayout.Label(label, GUILayout.Width(130f));
            uncommitted = GUILayout.TextField(uncommitted, maxLength: 12, GUILayout.Width(100f));
            GUILayout.EndHorizontal();

            return float.TryParse(uncommitted, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed : value;
        }

        public void Draw()
        {
            GUILayout.BeginVertical(GUILayout.ExpandWidth(false));

            if (uncommittedPort == "initial")
                uncommittedPort = serverPort.ToString();

            GUILayout.Label($"Network port (1024{EnDash}65535)");
            uncommittedPort = GUILayout.TextField(uncommittedPort, maxLength: 5);
            uncommittedPort = new string(uncommittedPort.Where(c => char.IsDigit(c)).ToArray());
            bool isValidPort = int.TryParse(uncommittedPort, out var parsed) && parsed >= 1024 && parsed <= 65535;

            GUILayout.BeginHorizontal();
            GUILayout.Label(message);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Password (blank for none)");
            serverPassword = GUILayout.TextField(serverPassword);
            GUILayout.EndHorizontal();

            permissions.Draw();

            var newShowUndiscoveredLocomotives = GUILayout.Toggle(
                showUndiscoveredLocomotives,
                "Show undiscovered locomotives");
            if (newShowUndiscoveredLocomotives != showUndiscoveredLocomotives)
            {
                showUndiscoveredLocomotives = newShowUndiscoveredLocomotives;
                CarUpdater.ForceCarRefresh();
            }

            GUILayout.Space(6f);
            GUILayout.Label("Signal HUD:");
            showSignalHud = GUILayout.Toggle(showSignalHud,
                "Show in-game speed sign and signal HUD");

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            GUILayout.Label("Toggle HUD hotkey", GUILayout.Width(130f));
            var hotkeyLabel = capturingSignalHudHotkey
                ? "Press a key..."
                : signalHudHotkey == KeyCode.None ? "Not bound" : signalHudHotkey.ToString();
            if (GUILayout.Button(hotkeyLabel, GUILayout.Width(150f)))
                capturingSignalHudHotkey = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Click the button and press a key. Backspace/Delete clears the binding.");

            if (capturingSignalHudHotkey && Event.current.type == EventType.KeyDown)
            {
                var pressed = Event.current.keyCode;
                if (pressed == KeyCode.Escape)
                {
                    capturingSignalHudHotkey = false;
                }
                else if (pressed == KeyCode.Backspace || pressed == KeyCode.Delete)
                {
                    signalHudHotkey = KeyCode.None;
                    capturingSignalHudHotkey = false;
                }
                else if (pressed != KeyCode.None)
                {
                    signalHudHotkey = pressed;
                    capturingSignalHudHotkey = false;
                }
                Event.current.Use();
            }

            GUILayout.Space(6f);
            GUILayout.Label("Map picture:");
            GUILayout.Label("An image drawn under the railway - a community map or a render."
                + " Derail Valley is not a real place, so there is no aerial photography of it"
                + " to fetch, and the terrain the game does have cannot be read from here.");
            showMapImage = GUILayout.Toggle(showMapImage, "Show the picture behind the map");

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            GUILayout.Label("Image file", GUILayout.Width(130f));
            mapImagePath = GUILayout.TextField(mapImagePath ?? "", GUILayout.Width(360f));
            GUILayout.EndHorizontal();
            GUILayout.Label("Full path to a .png, .jpg or .webp file.");

            GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));
            GUILayout.Label("Opacity", GUILayout.Width(130f));
            mapImageOpacity = GUILayout.HorizontalSlider(
                mapImageOpacity, 0f, 1f, GUILayout.Width(200f));
            GUILayout.Label(Mathf.RoundToInt(mapImageOpacity * 100f) + "%", GUILayout.Width(50f));
            GUILayout.EndHorizontal();

            // The corners are in the same absolute world metres the track
            // geometry is drawn in, so a picture lined up once stays lined up
            // however far the player travels.
            GUILayout.Label("Corners, in world metres:");
            mapImageMinX = NumberField("West (min X)", mapImageMinX, ref uncommittedMinX);
            mapImageMaxX = NumberField("East (max X)", mapImageMaxX, ref uncommittedMaxX);
            mapImageMinZ = NumberField("South (min Z)", mapImageMinZ, ref uncommittedMinZ);
            mapImageMaxZ = NumberField("North (max Z)", mapImageMaxZ, ref uncommittedMaxZ);
            GUILayout.Label("The map page reports where the rails actually are, so the corners"
                + " can be lined up against something rather than guessed at.");

            GUILayout.Space(6f);
            enableLogging = GUILayout.Toggle(enableLogging, "Enable logging");

            GUILayout.EndVertical();
        }

        override public void Save(UnityModManager.ModEntry entry)
        {
            Save<Settings>(this, entry);
        }
    }

    public class Permissions
    {
        public class PlayerPermissions
        {
            public string name;
            public bool canToggleJunctions;
            public bool canControlLocomotives;

            public PlayerPermissions()
            {
                name = "";
            }

            public PlayerPermissions(string name)
            {
                this.name = name;
            }
        }

        public readonly List<PlayerPermissions> permissions = new List<PlayerPermissions>();

        public Permissions()
        {
            Sessions.OnSessionStarted += OnSessionStarted;
        }

        public bool HasJunctionPermission(string username)
        {
            return permissions.Find(p => p.name == username)?.canToggleJunctions ?? false;
        }

        public bool HasLocoControlPermission(string username)
        {
            return permissions.Find(p => p.name == username)?.canControlLocomotives ?? false;
        }

        private void OnSessionStarted(string username)
        {
            if (!permissions.Any(p => p.name == username))
            {
                permissions.Add(new PlayerPermissions(username));
                permissions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.name, b.name));
            }
        }

        public void Draw()
        {
            GUILayout.Label("Dispatcher permissions:");
            GUILayout.BeginHorizontal("box", GUILayout.ExpandWidth(false));
            DrawNamesColumn();
            DrawConnectedColumn();
            DrawJunctionsColumn();
            DrawLocoControlColumn();
            GUILayout.EndHorizontal();
        }

        private void DrawColumn(string label, Action<PlayerPermissions> action)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(label);
            foreach (var p in permissions)
                action(p);
            GUILayout.EndVertical();
        }

        private void DrawNamesColumn()
        {
            DrawColumn("Name", p => GUILayout.Label(p.name));
        }

        private void DrawConnectedColumn()
        {
            var connectedUsers = Sessions.GetUsersWithActiveSessions();
            DrawColumn("Connected", p => GUILayout.Toggle(connectedUsers.Contains(p.name), ""));
        }

        private void DrawJunctionsColumn()
        {
            DrawColumn("Junctions", p => p.canToggleJunctions = GUILayout.Toggle(p.canToggleJunctions, ""));
        }

        private void DrawLocoControlColumn()
        {
            DrawColumn("Locomotive Control", p => p.canControlLocomotives = GUILayout.Toggle(p.canControlLocomotives, ""));
        }
    }
}
