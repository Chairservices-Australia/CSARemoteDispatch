using System;
using System.IO;
using System.Linq;
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

        private static IServer? registeredServer;
        private static IClient? registeredClient;
        private static long nextRevision;
        private static long lastReceivedRevision = -1;
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

        /// The name the Multiplayer mod knows this player by, or empty when it
        /// is not loaded or has no name to give.
        ///
        /// Read by reflection rather than against a named property. The API
        /// carries Username and DisplayName but exposes no "this is me"
        /// accessor, and where the local name hangs has moved between releases;
        /// a dispatcher's name on a card is not worth a hard binding that stops
        /// the mod loading when it moves again. Anything not found simply falls
        /// back, so the worst case is the label this replaced.
        public static string LocalPlayerName()
        {
            if (!Present)
                return "";
            if (cachedLocalName == null)
                cachedLocalName = LocalPlayerNameCore();
            return cachedLocalName;
        }

        private static string? cachedLocalName;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string LocalPlayerNameCore()
        {
            try
            {
                foreach (var source in new object?[] { MultiplayerAPI.Client, MultiplayerAPI.Instance })
                {
                    var name = NameOn(source);
                    if (name.Length > 0)
                        return name;
                }
            }
            catch (Exception e)
            {
                Main.DebugLog(() => "Could not read the multiplayer player name: " + e.Message);
            }
            return "";
        }

        private static string NameOn(object? source)
        {
            if (source == null)
                return "";
            var type = source.GetType();
            foreach (var property in new[] { "DisplayName", "Username" })
            {
                var found = type.GetProperty(property);
                if (found == null || found.PropertyType != typeof(string))
                    continue;
                var value = found.GetValue(source, null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value!.Trim();
            }
            return "";
        }

        public static string MirroredRoutesJson => mirroredRoutesJson;

        public static void Reset()
        {
            mirroredRoutesJson = "[]";
            nextRevision = 0;
            lastReceivedRevision = -1;
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
            MultiplayerAPI.ServerStopped += OnServerStopped;
            MultiplayerAPI.ClientStopped += OnClientStopped;
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
            MultiplayerAPI.ServerStopped -= OnServerStopped;
            MultiplayerAPI.ClientStopped -= OnClientStopped;
            OnServerStopped();
            OnClientStopped();
        }

        private static void OnServerStopped()
        {
            if (registeredServer != null)
                registeredServer.OnPlayerReady -= OnPlayerReady;
            registeredServer = null;
        }

        private static void OnClientStopped()
        {
            registeredClient = null;
            mirroredRoutesJson = "[]";
            lastReceivedRevision = -1;
        }

        private static void OnServerStarted(IServer server)
        {
            if (server == null || ReferenceEquals(registeredServer, server))
                return;
            OnServerStopped();
            registeredServer = server;
            server.RegisterSerializablePacket<RouteRequestPacket>(OnRouteRequested);
            server.RegisterSerializablePacket<RouteClearPacket>(OnClearRequested);
            server.RegisterSerializablePacket<RouteSyncRequestPacket>(OnSyncRequested);
            server.OnPlayerReady += OnPlayerReady;
            Main.DebugLog(() => "Route networking: registered host handlers");
        }

        private static void OnClientStarted(IClient client)
        {
            if (client == null || ReferenceEquals(registeredClient, client))
                return;
            registeredClient = client;
            client.RegisterSerializablePacket<RouteStatePacket>(OnRouteStateReceived);
            client.SendSerializablePacketToServer(new RouteSyncRequestPacket());
            Main.DebugLog(() => "Route networking: registered client handlers");
        }

        // ---------- host side ----------

        private static void OnRouteRequested(RouteRequestPacket packet, IPlayer sender)
        {
            // Packets arrive off the network thread; touching junctions, signals
            // or the route table has to happen where the rest of the game runs.
            var trainsetId = packet.trainsetId;
            var trainCarGuid = packet.trainCarGuid;
            ResolveHostTrain(packet.trainCarNetId, ref trainsetId, ref trainCarGuid);
            var trackId = packet.destinationTrackId;
            if ((trainsetId < 0 && string.IsNullOrEmpty(trainCarGuid))
                || string.IsNullOrWhiteSpace(trackId) || trackId.Length > 256
                || trainCarGuid.Length > 128)
                return;
            var who = sender?.DisplayName ?? "a player";
            Updater.RunOnMainThread(() =>
            {
                Routing.SetRouteAsAuthority(trainsetId, trainCarGuid, trackId, who);
                Routing.PublishRoutes();
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResolveHostTrain(uint netId, ref int trainsetId, ref string trainCarGuid)
        {
            if (netId == 0 || MultiplayerAPI.Instance == null
                || !MultiplayerAPI.Instance.TryGetObjectFromNetId<TrainCar>(netId, out var car)
                || car == null || car.trainset == null)
                return;
            trainsetId = car.trainset.id;
            trainCarGuid = car.CarGUID;
        }

        private static void OnSyncRequested(RouteSyncRequestPacket packet, IPlayer sender) =>
            SendSnapshotTo(sender);

        private static void OnPlayerReady(IPlayer player) => SendSnapshotTo(player);

        private static void SendSnapshotTo(IPlayer player)
        {
            if (player == null)
                return;
            Updater.RunOnMainThread(() =>
            {
                var server = registeredServer;
                if (server != null)
                    server.SendSerializablePacketToPlayer(new RouteStatePacket
                    {
                        revision = nextRevision,
                        json = Routing.AllRoutesJson(),
                    }, player);
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
            server.SendSerializablePacketToAll(new RouteStatePacket
            {
                revision = ++nextRevision,
                json = json,
            });
        }

        // ---------- client side ----------

        private static void OnRouteStateReceived(RouteStatePacket packet)
        {
            if (packet.revision < lastReceivedRevision)
                return;
            lastReceivedRevision = packet.revision;
            var json = packet.json ?? "[]";
            if (json.Length > 1024 * 1024)
                return;
            // This mirror and the web session queues are lock-protected/plain
            // managed data; avoiding a main-thread hop shortens route latency.
            mirroredRoutesJson = json;
            Sessions.AddTag("routes");
        }

        /// Ask the host to lay a road. Returns false when there is nobody to ask.
        public static bool RequestRoute(Trainset trainset, string destinationTrackId)
        {
            if (!Present || !IsRemoteClient)
                return false;
            return RequestRouteCore(trainset, destinationTrackId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RequestRouteCore(Trainset trainset, string destinationTrackId)
        {
            var client = MultiplayerAPI.Client;
            if (client == null || !client.IsConnected)
                return false;
            var anchor = trainset.cars?.FirstOrDefault(car => car != null);
            uint netId = 0;
            if (anchor != null && MultiplayerAPI.Instance != null)
                MultiplayerAPI.Instance.TryGetNetId(anchor, out netId);
            client.SendSerializablePacketToServer(new RouteRequestPacket
            {
                trainsetId = trainset.id,
                trainCarNetId = netId,
                trainCarGuid = anchor?.CarGUID ?? "",
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
            public uint trainCarNetId;
            public string trainCarGuid = "";
            public string destinationTrackId = "";

            public void Serialize(BinaryWriter writer)
            {
                writer.Write(trainsetId);
                writer.Write(trainCarNetId);
                writer.Write(trainCarGuid ?? "");
                writer.Write(destinationTrackId ?? "");
            }

            public void Deserialize(BinaryReader reader)
            {
                trainsetId = reader.ReadInt32();
                trainCarNetId = reader.ReadUInt32();
                trainCarGuid = reader.ReadString();
                destinationTrackId = reader.ReadString();
            }
        }

        public class RouteClearPacket : ISerializablePacket
        {
            public string routeId = "";

            public void Serialize(BinaryWriter writer) => writer.Write(routeId ?? "");
            public void Deserialize(BinaryReader reader) => routeId = reader.ReadString();
        }

        public class RouteSyncRequestPacket : ISerializablePacket
        {
            public void Serialize(BinaryWriter writer) { }
            public void Deserialize(BinaryReader reader) { }
        }

        /// The host's whole route list, as the JSON the page already understands.
        ///
        /// Sent as one document rather than field by field: the shape is decided
        /// by what the page renders, and duplicating it in packet code is a
        /// second definition to keep in step for no gain. It goes reliably, so
        /// the transport fragments it when the list is long.
        public class RouteStatePacket : ISerializablePacket
        {
            public long revision;
            public string json = "[]";

            public void Serialize(BinaryWriter writer)
            {
                writer.Write(revision);
                writer.Write(json ?? "[]");
            }

            public void Deserialize(BinaryReader reader)
            {
                revision = reader.ReadInt64();
                json = reader.ReadString();
            }
        }
    }
}
