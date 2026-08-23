using System.IO;
using System.Runtime.CompilerServices;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using UnityModManagerNet;

namespace DvMod.RemoteDispatch
{
    /// Routing across a multiplayer session.
    ///
    /// The world is the host's: only it may throw a junction with authority, and
    /// only DV Signals running there may reserve a signal. A client that laid its
    /// own road would be setting switches nobody else knew about and reserving
    /// signals nobody else could see, which is why route allocation used to be
    /// refused outright on a client.
    ///
    /// Instead every player's request is sent to the host, which plans and
    /// allocates it against every other route it already holds - so the existing
    /// conflict handling covers other players' trains for free - and then
    /// broadcasts the resulting list back. A client's own page shows the host's
    /// answer rather than a private guess.
    public static class RouteNetwork
    {
        /// The host's view of every route, as served to a client's own web UI.
        private static string mirroredRoutesJson = "[]";

        private static bool registeredServer;
        private static bool registeredClient;
        private static bool? multiplayerPresent;

        /// True when the Multiplayer mod is loaded at all.
        ///
        /// Checked through Unity Mod Manager first, because merely touching a
        /// type from MultiplayerAPI.dll loads that assembly - which throws when
        /// the mod is not installed. Everything past this point is kept in its
        /// own non-inlined method so the runtime only resolves those types once
        /// the mod is known to be there.
        public static bool Present
        {
            get
            {
                if (multiplayerPresent == null)
                    multiplayerPresent = UnityModManager.FindMod("Multiplayer") != null && ApiLoaded();
                return multiplayerPresent.Value;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ApiLoaded() => MultiplayerAPI.IsMultiplayerLoaded;

        /// True when a session is running and this instance is not the host, so
        /// planning must be handed over rather than done here.
        public static bool IsRemoteClient => Present && IsRemoteClientCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsRemoteClientCore() =>
            MultiplayerAPI.Instance != null
            && MultiplayerAPI.Instance.IsConnected
            && !MultiplayerAPI.Instance.IsHost;

        /// True when this instance owns the world and should do the allocating.
        public static bool IsAuthority => !Present || IsAuthorityCore();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsAuthorityCore()
        {
            var api = MultiplayerAPI.Instance;
            return api == null || !api.IsConnected || api.IsHost;
        }

        public static string MirroredRoutesJson => mirroredRoutesJson;

        public static void Reset()
        {
            mirroredRoutesJson = "[]";
        }

        public static void Initialise()
        {
            if (!Present)
                return;
            Hook();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Hook()
        {
            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            if (MultiplayerAPI.Server != null)
                OnServerStarted(MultiplayerAPI.Server);
            if (MultiplayerAPI.Client != null)
                OnClientStarted(MultiplayerAPI.Client);
        }

        public static void Shutdown()
        {
            if (!Present)
                return;
            Unhook();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Unhook()
        {
            MultiplayerAPI.ServerStarted -= OnServerStarted;
            MultiplayerAPI.ClientStarted -= OnClientStarted;
            registeredServer = false;
            registeredClient = false;
        }

        private static void OnServerStarted(IServer server)
        {
            if (registeredServer || server == null)
                return;
            registeredServer = true;
            server.RegisterSerializablePacket<RouteRequestPacket>(OnRouteRequested);
            server.RegisterSerializablePacket<RouteClearPacket>(OnClearRequested);
            Main.DebugLog(() => "Route networking: registered host handlers");
        }

        private static void OnClientStarted(IClient client)
        {
            if (registeredClient || client == null)
                return;
            registeredClient = true;
            client.RegisterSerializablePacket<RouteStatePacket>(OnRouteStateReceived);
            Main.DebugLog(() => "Route networking: registered client handlers");
        }

        // ---------- host side ----------

        private static void OnRouteRequested(RouteRequestPacket packet, IPlayer sender)
        {
            // Packets arrive off the network thread; touching junctions, signals
            // or the route table has to happen where the rest of the game runs.
            var trainsetId = packet.trainsetId;
            var trackId = packet.destinationTrackId;
            var who = sender?.DisplayName ?? "a player";
            Updater.RunOnMainThread(() =>
            {
                Routing.SetRouteAsAuthority(trainsetId, trackId, who);
                Routing.PublishRoutes();
            });
        }

        private static void OnClearRequested(RouteClearPacket packet, IPlayer sender)
        {
            var routeId = packet.routeId;
            Updater.RunOnMainThread(() =>
            {
                Routing.ClearRoute(routeId);
                Routing.PublishRoutes();
            });
        }

        /// Send the host's route list to everyone. Called whenever it changes,
        /// which includes the per-second pass that extends a road held short of
        /// another train.
        public static void BroadcastRoutes(string json)
        {
            if (!Present || !IsAuthority)
                return;
            BroadcastRoutesCore(json);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BroadcastRoutesCore(string json)
        {
            var server = MultiplayerAPI.Server;
            if (server == null)
                return;
            server.SendSerializablePacketToAll(new RouteStatePacket { json = json });
        }

        // ---------- client side ----------

        private static void OnRouteStateReceived(RouteStatePacket packet)
        {
            var json = packet.json ?? "[]";
            Updater.RunOnMainThread(() =>
            {
                mirroredRoutesJson = json;
                // The page is driven by the tag stream, so it refreshes on its
                // own once the mirror has been replaced.
                Sessions.AddTag("routes");
            });
        }

        /// Ask the host to lay a road. Returns false when there is nobody to ask.
        public static bool RequestRoute(int trainsetId, string destinationTrackId)
        {
            if (!Present || !IsRemoteClient)
                return false;
            return RequestRouteCore(trainsetId, destinationTrackId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RequestRouteCore(int trainsetId, string destinationTrackId)
        {
            var client = MultiplayerAPI.Client;
            if (client == null || !client.IsConnected)
                return false;
            client.SendSerializablePacketToServer(new RouteRequestPacket
            {
                trainsetId = trainsetId,
                destinationTrackId = destinationTrackId,
            });
            return true;
        }

        public static bool RequestClear(string routeId)
        {
            if (!Present || !IsRemoteClient)
                return false;
            return RequestClearCore(routeId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RequestClearCore(string routeId)
        {
            var client = MultiplayerAPI.Client;
            if (client == null || !client.IsConnected)
                return false;
            client.SendSerializablePacketToServer(new RouteClearPacket { routeId = routeId });
            return true;
        }

        // ---------- packets ----------

        public class RouteRequestPacket : ISerializablePacket
        {
            public int trainsetId;
            public string destinationTrackId = "";

            public void Serialize(BinaryWriter writer)
            {
                writer.Write(trainsetId);
                writer.Write(destinationTrackId ?? "");
            }

            public void Deserialize(BinaryReader reader)
            {
                trainsetId = reader.ReadInt32();
                destinationTrackId = reader.ReadString();
            }
        }

        public class RouteClearPacket : ISerializablePacket
        {
            public string routeId = "";

            public void Serialize(BinaryWriter writer) => writer.Write(routeId ?? "");
            public void Deserialize(BinaryReader reader) => routeId = reader.ReadString();
        }

        /// The host's whole route list, as the JSON the page already understands.
        ///
        /// Sent as one document rather than field by field: the shape is decided
        /// by what the page renders, and duplicating it in packet code is a
        /// second definition to keep in step for no gain. It goes reliably, so
        /// the transport fragments it when the list is long.
        public class RouteStatePacket : ISerializablePacket
        {
            public string json = "[]";

            public void Serialize(BinaryWriter writer) => writer.Write(json ?? "[]");
            public void Deserialize(BinaryReader reader) => json = reader.ReadString();
        }
    }
}
