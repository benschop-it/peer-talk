﻿using Common.Logging;
using Ipfs;
using PeerTalk;
using PeerTalk.Protocols;
using ProtoBuf;
using Semver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace PeerTalk.Routing
{
    /// <summary>
    ///   DHT Protocol version 1.0
    /// </summary>
    public class Dht1 : IPeerProtocol, IService, IPeerRouting, IContentRouting
    {
        static ILog log = LogManager.GetLogger(typeof(Dht1));

        /// <inheritdoc />
        public string Name { get; } = "ipfs/kad";

        /// <inheritdoc />
        public SemVersion Version { get; } = new SemVersion(1, 0);

        /// <summary>
        ///   Provides access to other peers.
        /// </summary>
        public Swarm Swarm { get; set; }

        /// <summary>
        ///  Routing information on peers.
        /// </summary>
        public RoutingTable RoutingTable;

        /// <inheritdoc />
        public override string ToString()
        {
            return $"/{Name}/{Version}";
        }

        /// <inheritdoc />
        public async Task ProcessMessageAsync(PeerConnection connection, Stream stream, CancellationToken cancel = default(CancellationToken))
        {
            var request = await ProtoBufHelper.ReadMessageAsync<DhtMessage>(stream, cancel);

            log.Debug($"got message from {connection.RemotePeer}");
            // TODO: process the request
        }

        /// <inheritdoc />
        public Task StartAsync()
        {
            log.Debug("Starting");

            RoutingTable = new RoutingTable(Swarm.LocalPeer);
            Swarm.AddProtocol(this);
            Swarm.PeerDiscovered += Swarm_PeerDiscovered;
            foreach (var peer in Swarm.KnownPeers)
            {
                RoutingTable.Add(peer);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync()
        {
            log.Debug("Stopping");

            Swarm.RemoveProtocol(this);
            Swarm.PeerDiscovered -= Swarm_PeerDiscovered;

            return Task.CompletedTask;
        }

        /// <summary>
        ///   The swarm has discovered a new peer, update the routing table.
        /// </summary>
        void Swarm_PeerDiscovered(object sender, Peer e)
        {
            RoutingTable.Add(e);
        }

        /// <inheritdoc />
        public async Task<Peer> FindPeerAsync(MultiHash id, CancellationToken cancel = default(CancellationToken))
        {
            // Can always find self.
            if (Swarm.LocalPeer.Id == id)
                return Swarm.LocalPeer;

            // Maybe the swarm knows about it.
            var found = Swarm.KnownPeers.FirstOrDefault(p => p.Id == id);
            if (found != null)
                return found;

            // Ask our peers for information of requested peer.
            var nearest = RoutingTable.NearestPeers(id);
            var query = new DhtMessage
            {
                Type = MessageType.FindNode,
                Key = id.ToArray()
            };
            log.Debug($"Query {query.Type}");
            foreach (var peer in nearest)
            {
                if (found != null)
                {
                    return found;
                }

                log.Debug($"Query peer {peer.Id} for {query.Type}");

                using (var stream = await Swarm.DialAsync(peer, this.ToString(), cancel))
                {
                    ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, query, PrefixStyle.Base128);
                    await stream.FlushAsync(cancel);
                    var response = await ProtoBufHelper.ReadMessageAsync<DhtMessage>(stream, cancel);
                    if (response.CloserPeers == null)
                    {
                        continue;
                    }
                    foreach (var closer in response.CloserPeers)
                    {
                        if (closer.TryToPeer(out Peer p))
                        {
                            p = Swarm.RegisterPeer(p);
                            if (id == p.Id)
                            {
                                log.Debug("Found answer");
                                found = p;
                            }
                        }
                    }
                }
            }

            // Unknown peer ID.
            throw new KeyNotFoundException($"Cannot locate peer '{id}'.");
        }

        /// <inheritdoc />
        public Task ProvideAsync(Cid cid, bool advertise = true, CancellationToken cancel = default(CancellationToken))
        {
            throw new NotImplementedException("DHT ProvideAsync");
        }

        /// <inheritdoc />
        public async Task<IEnumerable<Peer>> FindProvidersAsync(Cid id, int limit = 20, CancellationToken cancel = default(CancellationToken))
        {
            var providers = new List<Peer>();
            var visited = new List<Peer> { Swarm.LocalPeer };

            //var key = Encoding.ASCII.GetBytes(id.Encode());
            var key = id.Hash.ToArray();

            var query = new DhtMessage
            {
                Type = MessageType.GetProviders,
                Key = key
            };
            log.Debug($"Query {query.Type}");

            while (!cancel.IsCancellationRequested)
            {
                if (providers.Count >= limit)
                    break;

                // Get the nearest peers that havr not been visited.
                var peers = RoutingTable
                    .NearestPeers(id.Hash)
                    .Where(p => !visited.Contains(p))
                    .Take(3)
                    .ToArray();
                if (peers.Length == 0)
                    break;

                visited.AddRange(peers);

                try
                {
                    log.Debug($"Next {peers.Length} queries");
                    // Only allow 10 seconds per pass.
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel))
                    {
                        var tasks = peers.Select(p => FindProvidersAsync(p, id, query, providers, cts.Token));
                        await Task.WhenAll(tasks);
                    }
                }
                catch (Exception e)
                {
                    log.Warn("dquery failed", e); //eat it
                }
            }

            // All peers queried or the limit has been reached.
            log.Debug($"Found {providers.Count} providers, visited {visited.Count} peers");
            log.Debug($"{Swarm.KnownPeers.Count()} known peers");
            return providers.Take(limit);
        }

        async Task FindProvidersAsync(
            Peer peer,
            Cid id,
            DhtMessage query,
            List<Peer> providers,
            CancellationToken cancel)
        {
            try
            {
                // TODO: Is this reasonable to imposes a time limit on connection?
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var stream = await Swarm.DialAsync(peer, this.ToString(), cts.Token))
                {
                    // Send the KAD query and get a response.
                    ProtoBuf.Serializer.SerializeWithLengthPrefix(stream, query, PrefixStyle.Base128);
                    await stream.FlushAsync(cancel);
                    var response = await ProtoBufHelper.ReadMessageAsync<DhtMessage>(stream, cancel);

                    log.Debug($"Processing DHT response from {peer}");
                    if (response.CloserPeers != null)
                    {
                        foreach (var closer in response.CloserPeers)
                        {
                            if (closer.TryToPeer(out Peer p))
                            {
                                Swarm.RegisterPeer(p);
                            }
                        }
                        log.Debug($"Found {response.CloserPeers.Count()} closer peers");
                    }

                    if (response.ProviderPeers != null)
                    {
                        foreach (var provider in response.ProviderPeers)
                        {
                            if (provider.TryToPeer(out Peer p))
                            {
                                Console.WriteLine($"FOUND peer {p}");
                                // TODO:Only unique answers
                                providers.Add(Swarm.RegisterPeer(p));
                                // TODO: Stop the distributed query if the limit is reached.
                            }
                        }
                        log.Debug($"Found {response.ProviderPeers.Count()} provider peers");
                    }

                    log.Debug($"Done with DHT response from {peer}");
                }
            }
            catch (Exception e)
            {
                log.Warn("query failed", e); // eat it. Hopefully other peers will provide an answet.
            }
        }
    }
}
