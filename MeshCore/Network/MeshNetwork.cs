﻿/*
Technitium Mesh
Copyright (C) 2018  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using MeshCore.Message;
using MeshCore.Network.Connections;
using MeshCore.Network.DHT;
using MeshCore.Network.SecureChannel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Security.Cryptography;

namespace MeshCore.Network
{
    internal delegate void NetworkChanged(MeshNetwork network, BinaryNumber newNetworkId);

    public delegate void PeerNotification(MeshNetwork.Peer peer);
    public delegate void MessageNotification(MeshNetwork.Peer peer, MessageItem message);
    public delegate void SecureChannelFailed(MeshNetwork network, SecureChannelException ex);

    public enum MeshNetworkType : byte
    {
        Private = 1,
        Group = 2
    }

    public enum MeshNetworkStatus : byte
    {
        Offline = 1,
        Online = 2
    }

    /* PENDING-
     * file transfer - UI events pending
    */

    public class MeshNetwork : IDisposable
    {
        #region events

        public event PeerNotification PeerAdded;
        public event PeerNotification PeerTyping;
        public event PeerNotification GroupImageChanged;
        public event MessageNotification MessageReceived;
        public event MessageNotification MessageDeliveryNotification;
        public event SecureChannelFailed SecureChannelFailed;

        #endregion

        #region variables

        public const int MAX_MESSAGE_SIZE = SecureChannelStream.MAX_PACKET_SIZE - 32;
        const int DATA_STREAM_BUFFER_SIZE = 8 * 1024;

        const int RENEGOTIATE_AFTER_BYTES_SENT = 104857600; //100mb
        const int RENEGOTIATE_AFTER_SECONDS = 3600; //1hr

        const int PEER_SEARCH_TIMER_INTERVAL = 60000;

        static readonly byte[] USER_ID_MASK_INITIAL_SALT = new byte[] { 0x9B, 0x68, 0xA9, 0xAE, 0xDE, 0x04, 0x09, 0x2C, 0x18, 0xF1, 0xBF, 0x14, 0x8C, 0xC5, 0xEE, 0x08, 0x0D, 0x7A, 0x62, 0x7C, 0xD2, 0xB2, 0x4F, 0x1E, 0xFC, 0x28, 0x40, 0x6A, 0xDA, 0x18, 0x4A, 0xFE };
        static readonly byte[] NETWORK_SECRET_INITIAL_SALT = new byte[] { 0x28, 0x4B, 0xAC, 0x0D, 0x34, 0x58, 0xE4, 0x7C, 0x34, 0x0A, 0xA5, 0x4A, 0xF1, 0xC8, 0x21, 0xC5, 0x69, 0x4C, 0x98, 0x29, 0x77, 0xAE, 0xED, 0x93, 0xBF, 0xC6, 0x5E, 0x2D, 0x3D, 0xDF, 0xE4, 0x47 };

        readonly ConnectionManager _connectionManager;
        readonly MeshNetworkType _type; //serialize 
        readonly BinaryNumber _userId; //serialize 
        readonly string _networkName; //serialize - only for group chat
        string _sharedSecret; //serialize
        MeshNetworkStatus _status; //serialize

        BinaryNumber _networkId; //serialize - for loading performance reasons
        BinaryNumber _networkSecret; //serialize - for loading performance reasons

        //encrypted message store
        readonly string _messageStoreId; //serialize
        readonly byte[] _messageStoreKey; //serialize
        MessageStore _store;

        //feature to run the network on local LAN network only
        DateTime _localNetworkOnlyDateModified; //serialize
        bool _localNetworkOnly; //serialize

        //group display image
        DateTime _groupDisplayImageDateModified; //serialize
        byte[] _groupDisplayImage = new byte[] { }; //serialize

        //feature to disallow new users from joining group
        DateTime _groupLockNetworkDateModified; //serialize
        bool _groupLockNetwork; //serialize

        //feature to let ui know to mute notifications for this network
        bool _mute; //serialize

        Peer _selfPeer;
        Peer _otherPeer; //serialize; only for private chat
        Dictionary<BinaryNumber, Peer> _peers; //serialize; only for group chat
        ReaderWriterLockSlim _peersLock; //only for group chat

        //peer search & announce timer
        Timer _peerSearchTimer;

        //ping keep-alive timer
        const int PING_TIMER_INTERVAL = 15000;
        Timer _pingTimer;

        #endregion

        #region constructor

        public MeshNetwork(ConnectionManager connectionManager, BinaryNumber userId, BinaryNumber peerUserId, string peerDisplayName, bool localNetworkOnly, string invitationMessage)
            : this(connectionManager, userId, peerUserId, peerDisplayName, localNetworkOnly, MeshNetworkStatus.Online, invitationMessage)
        { }

        private MeshNetwork(ConnectionManager connectionManager, BinaryNumber userId, BinaryNumber peerUserId, string peerDisplayName, bool localNetworkOnly, MeshNetworkStatus status, string invitationMessage)
        {
            _connectionManager = connectionManager;
            _type = MeshNetworkType.Private;
            _userId = userId;
            _status = status;
            _localNetworkOnly = localNetworkOnly;

            //generate id
            _networkId = GetPrivateNetworkId(_userId, peerUserId, _sharedSecret);
            _networkSecret = GetPrivateNetworkSecret(_userId, peerUserId, _sharedSecret);

            _messageStoreId = BinaryNumber.GenerateRandomNumber256().ToString();
            _messageStoreKey = BinaryNumber.GenerateRandomNumber256().Value;

            InitMeshNetwork(new MeshNetworkPeerInfo[] { new MeshNetworkPeerInfo(peerUserId, peerDisplayName, null) });

            //save invitation message to store
            if (invitationMessage != null)
            {
                MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, new MessageRecipient[] { new MessageRecipient(peerUserId) }, MessageType.TextMessage, invitationMessage, null, null, 0, null);
                msg.WriteTo(_store);
            }
        }

        public MeshNetwork(ConnectionManager connectionManager, BinaryNumber userId, string networkName, string sharedSecret, bool localNetworkOnly)
        {
            _connectionManager = connectionManager;
            _type = MeshNetworkType.Group;
            _userId = userId;
            _networkName = networkName;
            _sharedSecret = sharedSecret;
            _status = MeshNetworkStatus.Online;
            _localNetworkOnly = localNetworkOnly;

            //generate ids
            _networkId = GetGroupNetworkId(_networkName, _sharedSecret);
            _networkSecret = GetGroupNetworkSecret(_networkName, _sharedSecret);

            _messageStoreId = BinaryNumber.GenerateRandomNumber256().ToString();
            _messageStoreKey = BinaryNumber.GenerateRandomNumber256().Value;

            InitMeshNetwork();
        }

        public MeshNetwork(ConnectionManager connectionManager, BinaryReader bR)
        {
            _connectionManager = connectionManager;

            //parse
            switch (bR.ReadByte()) //version
            {
                case 1:
                    _type = (MeshNetworkType)bR.ReadByte();
                    _userId = new BinaryNumber(bR.BaseStream);

                    if (_type == MeshNetworkType.Group)
                        _networkName = bR.ReadShortString();

                    _sharedSecret = bR.ReadShortString();
                    _status = (MeshNetworkStatus)bR.ReadByte();

                    //
                    _networkId = new BinaryNumber(bR.BaseStream);
                    _networkSecret = new BinaryNumber(bR.BaseStream);

                    //
                    _messageStoreId = bR.ReadShortString();
                    _messageStoreKey = bR.ReadBuffer();

                    //
                    _localNetworkOnlyDateModified = bR.ReadDate();
                    _localNetworkOnly = bR.ReadBoolean();

                    //
                    _groupDisplayImageDateModified = bR.ReadDate();
                    _groupDisplayImage = bR.ReadBuffer();

                    //
                    _groupLockNetworkDateModified = bR.ReadDate();
                    _groupLockNetwork = bR.ReadBoolean();

                    //
                    _mute = bR.ReadBoolean();

                    //known peers
                    MeshNetworkPeerInfo[] knownPeers;

                    if (_type == MeshNetworkType.Private)
                    {
                        knownPeers = new MeshNetworkPeerInfo[] { new MeshNetworkPeerInfo(bR) };
                    }
                    else
                    {
                        knownPeers = new MeshNetworkPeerInfo[bR.ReadByte()];

                        for (int i = 0; i < knownPeers.Length; i++)
                            knownPeers[i] = new MeshNetworkPeerInfo(bR);
                    }

                    InitMeshNetwork(knownPeers);
                    break;

                default:
                    throw new InvalidDataException("MeshNetwork format version not supported.");
            }
        }

        #endregion

        #region IDisposable support

        bool _disposed = false;

        public void Dispose()
        {
            lock (this)
            {
                if (!_disposed)
                {
                    //stop ping timer
                    if (_pingTimer != null)
                        _pingTimer.Dispose();

                    //stop peer search & announce timer
                    if (_peerSearchTimer != null)
                        _peerSearchTimer.Dispose();

                    //dispose all peers
                    if (_peersLock != null)
                    {
                        _peersLock.EnterWriteLock();
                        try
                        {
                            foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                            {
                                try
                                {
                                    peer.Value.Dispose();
                                }
                                catch
                                { }
                            }

                            _peers.Clear();
                        }
                        finally
                        {
                            _peersLock.ExitWriteLock();
                        }

                        _peersLock.Dispose();
                    }

                    //close message store
                    if (_store != null)
                        _store.Dispose();

                    if (_selfPeer != null)
                        _selfPeer.Dispose();

                    if (_otherPeer != null)
                        _otherPeer.Dispose();

                    _disposed = true;
                }
            }
        }

        #endregion

        #region private event functions

        private void RaiseEventPeerAdded(Peer peer)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                PeerAdded?.Invoke(peer);
            }, null);
        }

        private void RaiseEventPeerTyping(Peer peer)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                PeerTyping?.Invoke(peer);
            }, null);
        }

        private void RaiseEventGroupImageChanged(Peer peer)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                GroupImageChanged?.Invoke(peer);
            }, null);
        }

        private void RaiseEventMessageReceived(Peer peer, MessageItem message)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                MessageReceived?.Invoke(peer, message);
            }, null);
        }

        private void RaiseEventMessageDeliveryNotification(Peer peer, MessageItem message)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                MessageDeliveryNotification?.Invoke(peer, message);
            }, null);
        }

        private void RaiseEventSecureChannelFailed(SecureChannelException ex)
        {
            _connectionManager.Node.SynchronizationContext.Send(delegate (object state)
            {
                SecureChannelFailed?.Invoke(this, ex);
            }, null);
        }

        #endregion

        #region static

        internal static BinaryNumber GetMaskedUserId(BinaryNumber userId)
        {
            using (HMAC hmac = new HMACSHA256(userId.Value))
            {
                return new BinaryNumber(hmac.ComputeHash(USER_ID_MASK_INITIAL_SALT));
            }
        }

        internal static byte[] GetKdfValue32(byte[] password, byte[] salt, int c = 1, int m = 1 * 1024 * 1024)
        {
            using (PBKDF2 kdf1 = PBKDF2.CreateHMACSHA256(password, salt, c))
            {
                using (PBKDF2 kdf2 = PBKDF2.CreateHMACSHA256(password, kdf1.GetBytes(m), c))
                {
                    return kdf2.GetBytes(32);
                }
            }
        }

        private static BinaryNumber GetPrivateNetworkId(BinaryNumber userId1, BinaryNumber userId2, string sharedSecret)
        {
            return new BinaryNumber(GetKdfValue32(Encoding.UTF8.GetBytes(sharedSecret ?? ""), (userId1 ^ userId2).Value));
        }

        private static BinaryNumber GetGroupNetworkId(string networkName, string sharedSecret)
        {
            return new BinaryNumber(GetKdfValue32(Encoding.UTF8.GetBytes(sharedSecret ?? ""), Encoding.UTF8.GetBytes(networkName.ToLower())));
        }

        private static BinaryNumber GetPrivateNetworkSecret(BinaryNumber userId1, BinaryNumber userId2, string sharedSecret)
        {
            using (HMAC hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret ?? "")))
            {
                return new BinaryNumber(GetKdfValue32(hmac.ComputeHash(NETWORK_SECRET_INITIAL_SALT), (userId1 ^ userId2).Value));
            }
        }

        private static BinaryNumber GetGroupNetworkSecret(string networkName, string sharedSecret)
        {
            using (HMAC hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret ?? "")))
            {
                return new BinaryNumber(GetKdfValue32(hmac.ComputeHash(NETWORK_SECRET_INITIAL_SALT), Encoding.UTF8.GetBytes(networkName.ToLower())));
            }
        }

        internal static MeshNetwork AcceptPrivateNetworkInvitation(ConnectionManager connectionManager, Connection connection, BinaryNumber networkId, Stream channel)
        {
            //establish secure channel with untrusted client using psk as userId expecting the opposite side to know the userId
            using (SecureChannelStream secureChannel = new SecureChannelServerStream(channel, connection.RemotePeerEP, connection.ViaRemotePeerEP, RENEGOTIATE_AFTER_BYTES_SENT, RENEGOTIATE_AFTER_SECONDS, connectionManager.Node.SupportedCiphers, SecureChannelOptions.PRE_SHARED_KEY_AUTHENTICATION_REQUIRED | SecureChannelOptions.CLIENT_AUTHENTICATION_REQUIRED, connectionManager.Node.UserId.Value, connectionManager.Node.UserId, connectionManager.Node.PrivateKey, null))
            {
                //recv invitation text message
                MeshNetworkPacketMessage invitationMessage = MeshNetworkPacket.Parse(new BinaryReader(secureChannel)) as MeshNetworkPacketMessage;
                if (invitationMessage == null)
                    throw new MeshException("Invalid message received: expected invitation text message.");

                //create new private network with offline status
                MeshNetwork privateNetwork = new MeshNetwork(connectionManager, connectionManager.Node.UserId, secureChannel.RemotePeerUserId, secureChannel.RemotePeerUserId.ToString(), false, MeshNetworkStatus.Offline, null);

                if (privateNetwork._networkId != networkId)
                {
                    privateNetwork.Dispose();
                    throw new MeshException("Invalid network id detected.");
                }

                //store the invitation message in network store
                MessageItem msg = new MessageItem(secureChannel.RemotePeerUserId, invitationMessage);
                msg.WriteTo(privateNetwork._store);

                //send delivery notification
                new MeshNetworkPacketMessageDeliveryNotification(invitationMessage.MessageNumber).WriteTo(new BinaryWriter(secureChannel));

                return privateNetwork;
            }
        }

        #endregion

        #region private / internal

        private void InitMeshNetwork(MeshNetworkPeerInfo[] knownPeers = null)
        {
            //init message store
            string messageStoreFolder = Path.Combine(_connectionManager.Node.ProfileFolder, "messages");
            if (!Directory.Exists(messageStoreFolder))
                Directory.CreateDirectory(messageStoreFolder);

            _store = new MessageStore(new FileStream(Path.Combine(messageStoreFolder, _messageStoreId + ".index"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), new FileStream(Path.Combine(messageStoreFolder, _messageStoreId + ".data"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), _messageStoreKey);

            //load self as peer
            _selfPeer = new Peer(this, _userId, _connectionManager.Node.ProfileDisplayName);

            if (_type == MeshNetworkType.Private)
            {
                //load other peer
                _otherPeer = new Peer(this, knownPeers[0].PeerUserId, knownPeers[0].PeerDisplayName);

                if (knownPeers[0].PeerEPs != null)
                {
                    foreach (EndPoint peerEP in knownPeers[0].PeerEPs)
                        BeginMakeConnection(peerEP);
                }
            }
            else
            {
                _peers = new Dictionary<BinaryNumber, Peer>();
                _peersLock = new ReaderWriterLockSlim();

                //add self
                _peers.Add(_userId, _selfPeer);

                //load known peers
                if (knownPeers != null)
                {
                    foreach (MeshNetworkPeerInfo knownPeer in knownPeers)
                    {
                        _peers.Add(knownPeer.PeerUserId, new Peer(this, knownPeer.PeerUserId, knownPeer.PeerDisplayName));

                        if (knownPeer.PeerEPs != null)
                        {
                            foreach (EndPoint peerEP in knownPeer.PeerEPs)
                                BeginMakeConnection(peerEP);
                        }
                    }
                }
            }

            //init timers
            _peerSearchTimer = new Timer(PeerSearchAndAnnounceAsync, null, Timeout.Infinite, Timeout.Infinite);
            _pingTimer = new Timer(PingAsync, null, Timeout.Infinite, Timeout.Infinite);

            if (_status == MeshNetworkStatus.Online)
                GoOnline();
        }

        private void PeerSearchAndAnnounceAsync(object state)
        {
            if ((_type == MeshNetworkType.Private) && IsInvitationPending())
            {
                //find other peer via its masked user id to send invitation
                _connectionManager.DhtManager.BeginFindPeers(_otherPeer.MaskedPeerUserId, _localNetworkOnly, delegate (PeerEndPoint[] peerEPs)
                {
                    foreach (PeerEndPoint peerEP in peerEPs)
                        BeginMakeConnection(peerEP.EndPoint);
                });
            }
            else
            {
                _connectionManager.DhtManager.BeginAnnounce(_networkId, _localNetworkOnly, new PeerEndPoint(new IPEndPoint(IPAddress.Any, _connectionManager.LocalPort)), delegate (PeerEndPoint[] peerEPs)
                {
                    foreach (PeerEndPoint peerEP in peerEPs)
                        BeginMakeConnection(peerEP.EndPoint);
                });

                //register network on tcp relays. tcp relays will auto announce network over DHT and register their network end point
                _connectionManager.TcpRelayClientRegisterHostedNetwork(_networkId);
            }
        }

        private void PingAsync(object state)
        {
            try
            {
                SendMessageBroadcast(new MeshNetworkPacket(MeshNetworkPacketType.PingRequest));
            }
            catch
            { }
        }

        internal void BeginMakeConnection(EndPoint peerEP, Connection fallbackViaConnection = null)
        {
            if (_status == MeshNetworkStatus.Offline)
                return;

            if (_localNetworkOnly && ((peerEP.AddressFamily == AddressFamily.Unspecified) || !NetUtilities.IsPrivateIP((peerEP as IPEndPoint).Address)))
                return;

            Thread t = new Thread(delegate (object state)
            {
                try
                {
                    //make connection
                    Connection connection = _connectionManager.MakeConnection(peerEP);

                    EstablishSecureChannelAndJoinNetwork(connection);
                }
                catch
                {
                    if ((fallbackViaConnection != null) && !fallbackViaConnection.IsVirtualConnection)
                    {
                        try
                        {
                            //make virtual connection
                            Connection virtualConnection = _connectionManager.MakeVirtualConnection(fallbackViaConnection, peerEP);

                            EstablishSecureChannelAndJoinNetwork(virtualConnection);
                        }
                        catch
                        { }
                    }
                }
            });

            t.IsBackground = true;
            t.Start();
        }

        private void EstablishSecureChannelAndJoinNetwork(Connection connection)
        {
            try
            {
                //check if channel exists
                if (connection.ChannelExists(_networkId))
                    return;

                //request channel
                Stream channel = connection.ConnectMeshNetwork(_networkId);

                try
                {
                    byte[] psk;
                    ICollection<BinaryNumber> trustedUserIds;

                    switch (_type)
                    {
                        case MeshNetworkType.Private:
                            if (IsInvitationPending())
                                psk = _otherPeer.PeerUserId.Value;
                            else
                                psk = _networkSecret.Value;

                            trustedUserIds = new BinaryNumber[] { _otherPeer.PeerUserId };
                            break;

                        case MeshNetworkType.Group:
                            psk = _networkSecret.Value;

                            if (_groupLockNetwork)
                                trustedUserIds = GetKnownPeerUserIdList();
                            else
                                trustedUserIds = null;

                            break;

                        default:
                            throw new InvalidOperationException("Invalid network type.");
                    }

                    //establish secure channel
                    SecureChannelStream secureChannel = new SecureChannelClientStream(channel, connection.RemotePeerEP, connection.ViaRemotePeerEP, RENEGOTIATE_AFTER_BYTES_SENT, RENEGOTIATE_AFTER_SECONDS, _connectionManager.Node.SupportedCiphers, SecureChannelOptions.PRE_SHARED_KEY_AUTHENTICATION_REQUIRED | SecureChannelOptions.CLIENT_AUTHENTICATION_REQUIRED, psk, _userId, _connectionManager.Node.PrivateKey, trustedUserIds);

                    //join network
                    JoinNetwork(secureChannel, connection);
                }
                catch (SecureChannelException ex)
                {
                    channel.Dispose();

                    RaiseEventSecureChannelFailed(ex);
                }
                catch
                {
                    channel.Dispose();
                }
            }
            catch
            { }
        }

        internal void AcceptConnectionAndJoinNetwork(Connection connection, Stream channel)
        {
            try
            {
                if (_localNetworkOnly && ((connection.RemotePeerEP.AddressFamily == AddressFamily.Unspecified) || !NetUtilities.IsPrivateIP((connection.RemotePeerEP as IPEndPoint).Address)))
                {
                    channel.Dispose();
                    return;
                }

                //create secure channel
                ICollection<BinaryNumber> trustedUserIds;

                switch (_type)
                {
                    case MeshNetworkType.Private:
                        trustedUserIds = new BinaryNumber[] { _otherPeer.PeerUserId };
                        break;

                    case MeshNetworkType.Group:
                        if (_groupLockNetwork)
                            trustedUserIds = GetKnownPeerUserIdList();
                        else
                            trustedUserIds = null;

                        break;

                    default:
                        throw new InvalidOperationException("Invalid network type.");
                }

                SecureChannelStream secureChannel = new SecureChannelServerStream(channel, connection.RemotePeerEP, connection.ViaRemotePeerEP, RENEGOTIATE_AFTER_BYTES_SENT, RENEGOTIATE_AFTER_SECONDS, _connectionManager.Node.SupportedCiphers, SecureChannelOptions.PRE_SHARED_KEY_AUTHENTICATION_REQUIRED | SecureChannelOptions.CLIENT_AUTHENTICATION_REQUIRED, _networkSecret.Value, _userId, _connectionManager.Node.PrivateKey, trustedUserIds);

                //join network
                JoinNetwork(secureChannel, connection);
            }
            catch (SecureChannelException ex)
            {
                channel.Dispose();

                RaiseEventSecureChannelFailed(ex);
            }
            catch
            {
                channel.Dispose();
            }
        }

        private void JoinNetwork(SecureChannelStream channel, Connection connection)
        {
            if (_status == MeshNetworkStatus.Offline)
                throw new MeshException("Mesh network is offline.");

            Peer peer;

            if (_type == MeshNetworkType.Private)
            {
                if (channel.RemotePeerUserId.Equals(_otherPeer.PeerUserId))
                    peer = _otherPeer;
                else if (channel.RemotePeerUserId.Equals(_userId))
                    peer = _selfPeer;
                else
                    throw new InvalidOperationException();
            }
            else
            {
                bool peerAdded = false;

                _peersLock.EnterWriteLock();
                try
                {
                    BinaryNumber peerUserId = channel.RemotePeerUserId;

                    if (_peers.ContainsKey(peerUserId))
                    {
                        peer = _peers[peerUserId];
                    }
                    else
                    {
                        peer = new Peer(this, peerUserId, null);
                        _peers.Add(peerUserId, peer);

                        peerAdded = true;
                    }
                }
                finally
                {
                    _peersLock.ExitWriteLock();
                }

                if (peerAdded)
                    RaiseEventPeerAdded(peer);
            }

            peer.AddSession(channel, connection);

            if (_type == MeshNetworkType.Private)
                StopPeerSearch();
        }

        private ICollection<BinaryNumber> GetKnownPeerUserIdList()
        {
            if (_type == MeshNetworkType.Private)
            {
                return new BinaryNumber[] { _userId, _otherPeer.PeerUserId };
            }
            else
            {
                List<BinaryNumber> peerUserIdList;

                _peersLock.EnterReadLock();
                try
                {
                    peerUserIdList = new List<BinaryNumber>(_peers.Count);

                    foreach (KeyValuePair<BinaryNumber, Peer> item in _peers)
                        peerUserIdList.Add(item.Value.PeerUserId);
                }
                finally
                {
                    _peersLock.ExitReadLock();
                }

                return peerUserIdList;
            }
        }

        private void SendMessageBroadcast(byte[] data, int offset, int count)
        {
            if (_type == MeshNetworkType.Private)
            {
                _selfPeer.SendMessage(data, offset, count);
                _otherPeer.SendMessage(data, offset, count);
            }
            else
            {
                _peersLock.EnterReadLock();
                try
                {
                    foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                    {
                        if (peer.Value.IsOnline)
                            peer.Value.SendMessage(data, offset, count);
                    }
                }
                finally
                {
                    _peersLock.ExitReadLock();
                }
            }
        }

        private void SendMessageBroadcast(MeshNetworkPacket message)
        {
            using (MemoryStream mS = new MemoryStream())
            {
                message.WriteTo(new BinaryWriter(mS));

                byte[] buffer = mS.ToArray();
                SendMessageBroadcast(buffer, 0, buffer.Length);
            }
        }

        private void DoPeerExchange()
        {
            SendMessageBroadcast(new MeshNetworkPacketPeerExchange(_selfPeer.GetConnectedPeerList()));
        }

        private void UpdateConnectivityStatus()
        {
            lock (this)
            {
                List<MeshNetworkPeerInfo> uniquePeerInfoList = new List<MeshNetworkPeerInfo>();

                if (_type == MeshNetworkType.Private)
                {
                    //get each peer unique connections
                    uniquePeerInfoList.AddRange(_selfPeer.GetConnectedPeerList());

                    if (_otherPeer.IsOnline)
                    {
                        foreach (MeshNetworkPeerInfo info in _otherPeer.GetConnectedPeerList())
                        {
                            if (!uniquePeerInfoList.Contains(info))
                                uniquePeerInfoList.Add(info);
                        }
                    }

                    //update each peer connectivity status
                    _selfPeer.UpdateConnectivityStatus(uniquePeerInfoList);

                    if (_otherPeer.IsOnline)
                        _otherPeer.UpdateConnectivityStatus(uniquePeerInfoList);
                }
                else
                {
                    _peersLock.EnterReadLock();
                    try
                    {
                        //get each peer unique connections
                        foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                        {
                            if (peer.Value.IsOnline)
                            {
                                List<MeshNetworkPeerInfo> peerConnectedPeerInfo = peer.Value.GetConnectedPeerList();

                                foreach (MeshNetworkPeerInfo info in peerConnectedPeerInfo)
                                {
                                    if (!uniquePeerInfoList.Contains(info))
                                        uniquePeerInfoList.Add(info);
                                }
                            }
                        }

                        //update each peer connectivity status
                        foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                        {
                            if (peer.Value.IsOnline)
                                peer.Value.UpdateConnectivityStatus(uniquePeerInfoList);
                        }
                    }
                    finally
                    {
                        _peersLock.ExitReadLock();
                    }
                }
            }
        }

        internal void ProfileTriggerUpdate(bool profileImageUpdated)
        {
            _selfPeer.RaiseEventProfileChanged();

            MeshNode node = _connectionManager.Node;

            if (profileImageUpdated)
                SendMessageBroadcast(new MeshNetworkPacketProfileDisplayImage(node.ProfileDisplayImageDateModified, node.ProfileDisplayImage));
            else
                SendMessageBroadcast(new MeshNetworkPacketProfile(node.ProfileDateModified, node.ProfileDisplayName, node.ProfileStatus, node.ProfileStatusMessage));
        }

        internal void ProxyUpdated()
        {
            GoOffline();
            GoOnline();
        }

        private void StopPeerSearch()
        {
            if (_status == MeshNetworkStatus.Online)
                _peerSearchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void StartPeerSearch()
        {
            if (_status == MeshNetworkStatus.Online)
                _peerSearchTimer.Change(1000, PEER_SEARCH_TIMER_INTERVAL);
        }

        private bool IsInvitationPending()
        {
            int totalMessages = _store.GetMessageCount();
            if (totalMessages == 1)
            {
                MessageItem msg = new MessageItem(_store, 0);

                if ((msg.Type == MessageType.TextMessage) && msg.SenderUserId.Equals(_userId))
                {
                    if (msg.GetDeliveryStatus() != MessageDeliveryStatus.Delivered)
                        return true;
                }
            }

            return false;
        }

        private MessageRecipient[] GetMessageRecipients()
        {
            MessageRecipient[] msgRcpt;

            if (_type == MeshNetworkType.Private)
            {
                msgRcpt = new MessageRecipient[] { new MessageRecipient(_otherPeer.PeerUserId) };
            }
            else
            {
                _peersLock.EnterReadLock();
                try
                {
                    if (_peers.Count > 1)
                    {
                        msgRcpt = new MessageRecipient[_peers.Count - 1];
                        int i = 0;

                        foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                        {
                            if (!peer.Value.IsSelfPeer)
                                msgRcpt[i++] = new MessageRecipient(peer.Value.PeerUserId);
                        }
                    }
                    else
                    {
                        msgRcpt = new MessageRecipient[] { };
                    }
                }
                finally
                {
                    _peersLock.ExitReadLock();
                }
            }

            return msgRcpt;
        }

        #endregion

        #region public

        public void GoOnline()
        {
            if (_status != MeshNetworkStatus.Online)
            {
                //start timers
                _peerSearchTimer.Change(1000, PEER_SEARCH_TIMER_INTERVAL);
                _pingTimer.Change(Timeout.Infinite, PING_TIMER_INTERVAL);

                _status = MeshNetworkStatus.Online;
            }
        }

        public void GoOffline()
        {
            if (_status != MeshNetworkStatus.Offline)
            {
                //stop timers
                _peerSearchTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _pingTimer.Change(Timeout.Infinite, Timeout.Infinite);

                //disconnect all peers
                if (_type == MeshNetworkType.Private)
                {
                    _selfPeer.Disconnect();
                    _otherPeer.Disconnect();
                }
                else
                {
                    _peersLock.EnterReadLock();
                    try
                    {
                        foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                        {
                            peer.Value.Disconnect();
                        }
                    }
                    finally
                    {
                        _peersLock.ExitReadLock();
                    }
                }

                _status = MeshNetworkStatus.Offline;
            }
        }

        public Peer[] GetPeers()
        {
            if (_type == MeshNetworkType.Private)
            {
                return new Peer[] { _selfPeer, _otherPeer };
            }
            else
            {
                Peer[] peers;

                _peersLock.EnterReadLock();
                try
                {
                    peers = new Peer[_peers.Count];
                    _peers.Values.CopyTo(peers, 0);
                }
                finally
                {
                    _peersLock.ExitReadLock();
                }

                return peers;
            }
        }

        public void SaveInfoMessage(string info)
        {
            MessageItem msg = new MessageItem(info);
            msg.WriteTo(_store);

            if (MessageReceived != null)
                RaiseEventMessageReceived(_selfPeer, msg);
        }

        public void SendTypingNotification()
        {
            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                SendMessageBroadcast(new MeshNetworkPacket(MeshNetworkPacketType.MessageTypingNotification));
            });
        }

        public void SendTextMessage(string message)
        {
            MessageRecipient[] msgRcpt = GetMessageRecipients();

            MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, msgRcpt, MessageType.TextMessage, message, null, null, 0, null);
            msg.WriteTo(_store);

            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                SendMessageBroadcast(msg.GetMeshNetworkPacket());
            });

            RaiseEventMessageReceived(_selfPeer, msg);
        }

        public void SendInlineImage(string message, string filePath, byte[] imageThumbnail)
        {
            MessageRecipient[] msgRcpt = GetMessageRecipients();

            MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, msgRcpt, MessageType.InlineImage, message, imageThumbnail, Path.GetFileName(filePath), (new FileInfo(filePath)).Length, filePath);
            msg.WriteTo(_store);

            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                SendMessageBroadcast(msg.GetMeshNetworkPacket());
            });

            RaiseEventMessageReceived(_selfPeer, msg);
        }

        public void SendFileAttachment(string message, string filePath)
        {
            MessageRecipient[] msgRcpt = GetMessageRecipients();

            MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, msgRcpt, MessageType.FileAttachment, message, null, Path.GetFileName(filePath), (new FileInfo(filePath)).Length, filePath);
            msg.WriteTo(_store);

            ThreadPool.QueueUserWorkItem(delegate (object state)
            {
                SendMessageBroadcast(msg.GetMeshNetworkPacket());
            });

            RaiseEventMessageReceived(_selfPeer, msg);
        }

        public MessageItem[] GetLatestMessages(int index, int count)
        {
            return MessageItem.GetLatestMessageItems(_store, index, count);
        }

        public int GetMessageCount()
        {
            return _store.GetMessageCount();
        }

        public void DeleteNetwork()
        {
            //dispose
            this.Dispose();

            //delete message store index and data
            string messageStoreFolder = Path.Combine(_connectionManager.Node.ProfileFolder, "messages");

            try
            {
                File.Delete(Path.Combine(messageStoreFolder, _messageStoreId + ".index"));
            }
            catch
            { }

            try
            {
                File.Delete(Path.Combine(messageStoreFolder, _messageStoreId + ".data"));
            }
            catch
            { }

            //unregister network from tcp relays
            _connectionManager.TcpRelayClientUnregisterHostedNetwork(_networkId);

            //remove object from mesh node
            _connectionManager.Node.DeleteMeshNetwork(this);
        }

        public void WriteTo(BinaryWriter bW)
        {
            bW.Write((byte)1); //version

            bW.Write((byte)_type);
            _userId.WriteTo(bW.BaseStream);

            if (_type == MeshNetworkType.Group)
                bW.WriteShortString(_networkName);

            bW.WriteShortString(_sharedSecret);
            bW.Write((byte)_status);

            //
            _networkId.WriteTo(bW.BaseStream);
            _networkSecret.WriteTo(bW.BaseStream);

            //
            bW.WriteShortString(_messageStoreId);
            bW.WriteBuffer(_messageStoreKey);

            //
            bW.Write(_localNetworkOnlyDateModified);
            bW.Write(_localNetworkOnly);

            //
            bW.Write(_groupDisplayImageDateModified);
            bW.WriteBuffer(_groupDisplayImage);

            //
            bW.Write(_groupLockNetworkDateModified);
            bW.Write(_groupLockNetwork);

            //
            bW.Write(_mute);

            //known peers
            if (_type == MeshNetworkType.Private)
            {
                _otherPeer.GetPeerInfo().WriteTo(bW);
            }
            else
            {
                _peersLock.EnterReadLock();
                try
                {
                    bW.Write(Convert.ToByte(_peers.Count - 1)); //not counting self peer

                    foreach (KeyValuePair<BinaryNumber, Peer> peer in _peers)
                    {
                        if (!peer.Value.IsSelfPeer)
                            peer.Value.GetPeerInfo().WriteTo(bW);
                    }
                }
                finally
                {
                    _peersLock.ExitReadLock();
                }
            }
        }

        public override string ToString()
        {
            return this.NetworkDisplayName;
        }

        #endregion

        #region properties

        public MeshNode Node
        { get { return _connectionManager.Node; } }

        public MeshNetworkType Type
        { get { return _type; } }

        internal BinaryNumber NetworkId
        { get { return _networkId; } }

        public MeshNetworkStatus Status
        { get { return _status; } }

        public string NetworkName
        {
            get
            {
                if (_type == MeshNetworkType.Private)
                    return _otherPeer.PeerUserId.ToString();
                else
                    return _networkName;
            }
        }

        public string NetworkDisplayName
        {
            get
            {
                if (_type == MeshNetworkType.Private)
                {
                    if (_otherPeer.ProfileDisplayName != null)
                        return _otherPeer.ProfileDisplayName;

                    return _otherPeer.PeerUserId.ToString();
                }
                else
                {
                    return _networkName;
                }
            }
        }

        public string NetworkDisplayTitle
        {
            get
            {
                if (_type == MeshNetworkType.Private)
                {
                    if (_otherPeer.ProfileDisplayName != null)
                        return _otherPeer.ProfileDisplayName + " [" + _otherPeer.PeerUserId.ToString() + "]";

                    return _otherPeer.PeerUserId.ToString();
                }
                else
                {
                    return _networkName;
                }
            }
        }

        public string SharedSecret
        {
            get { return _sharedSecret; }
            set
            {
                BinaryNumber newNetworkId;
                BinaryNumber newNetworkSecret;

                switch (_type)
                {
                    case MeshNetworkType.Private:
                        newNetworkId = GetPrivateNetworkId(_userId, _otherPeer.PeerUserId, value);
                        newNetworkSecret = GetPrivateNetworkSecret(_userId, _otherPeer.PeerUserId, value);
                        break;

                    default:
                        newNetworkId = GetGroupNetworkId(_networkName, value);
                        newNetworkSecret = GetGroupNetworkSecret(_networkName, value);
                        break;
                }

                try
                {
                    _connectionManager.Node.MeshNetworkChanged(this, newNetworkId);

                    _sharedSecret = value;
                    _networkId = newNetworkId;
                    _networkSecret = newNetworkSecret;
                }
                catch (ArgumentException)
                {
                    throw new MeshException("Unable to change shared secret/password. Mesh network with same network id already exists.");
                }
            }
        }

        public Peer SelfPeer
        { get { return _selfPeer; } }

        public bool LocalNetworkOnly
        {
            get { return _localNetworkOnly; }
            set
            {
                _localNetworkOnlyDateModified = DateTime.UtcNow;
                _localNetworkOnly = value;

                //notify UI
                string infoText;

                if (_localNetworkOnly)
                    infoText = "Mesh group network updated to work only on local LAN networks.";
                else
                    infoText = "Mesh group network updated to work on Internet and local LAN networks.";

                MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, null, MessageType.Info, infoText, null, null, 0, null);
                msg.WriteTo(_store);

                RaiseEventMessageReceived(_selfPeer, msg);

                //notify peers
                SendMessageBroadcast(new MeshNetworkPacketLocalNetworkOnly(_localNetworkOnlyDateModified, _localNetworkOnly));
            }
        }

        public byte[] GroupDisplayImage
        {
            get { return _groupDisplayImage; }
            set
            {
                if (_type != MeshNetworkType.Group)
                    throw new InvalidOperationException("Cannot set group display image for non group network.");

                _groupDisplayImageDateModified = DateTime.UtcNow;
                _groupDisplayImage = value;

                //notify UI
                RaiseEventGroupImageChanged(_selfPeer);

                MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, null, MessageType.Info, "Group display image was updated.", null, null, 0, null);
                msg.WriteTo(_store);

                RaiseEventMessageReceived(_selfPeer, msg);

                //notify peers
                SendMessageBroadcast(new MeshNetworkPacketGroupDisplayImage(_groupDisplayImageDateModified, _groupDisplayImage));
            }
        }

        public bool GroupLockNetwork
        {
            get { return _groupLockNetwork; }
            set
            {
                if (_type != MeshNetworkType.Group)
                    throw new InvalidOperationException("Cannot set group lock network for non group network.");

                _groupLockNetworkDateModified = DateTime.UtcNow;
                _groupLockNetwork = value;

                //notify UI

                string infoText;

                if (_groupLockNetwork)
                    infoText = "Mesh group network was locked.";
                else
                    infoText = "Mesh group network was unlocked.";

                MessageItem msg = new MessageItem(DateTime.UtcNow, _userId, null, MessageType.Info, infoText, null, null, 0, null);
                msg.WriteTo(_store);

                RaiseEventMessageReceived(_selfPeer, msg);

                //notify peers
                SendMessageBroadcast(new MeshNetworkPacketGroupLockNetwork(_groupLockNetworkDateModified, _groupLockNetwork));
            }
        }

        public bool Mute
        {
            get { return _mute; }
            set { _mute = value; }
        }

        #endregion

        public enum PeerConnectivityStatus
        {
            NoNetwork = 0,
            PartialMeshNetwork = 1,
            FullMeshNetwork = 2
        }

        public class Peer : IDisposable
        {
            #region events

            public event EventHandler StateChanged;
            public event EventHandler ProfileChanged;
            public event EventHandler ConnectivityStatusChanged;

            #endregion

            #region variables

            readonly MeshNetwork _network;
            readonly BinaryNumber _peerUserId;

            readonly bool _isSelfPeer;
            bool _isOnline = false;

            PeerConnectivityStatus _connectivityStatus = PeerConnectivityStatus.NoNetwork;
            List<MeshNetworkPeerInfo> _connectedPeerList;
            List<MeshNetworkPeerInfo> _disconnectedPeerList;

            MeshNetworkPacketProfile _profile;
            MeshNetworkPacketProfileDisplayImage _profileImage;

            readonly List<Session> _sessions = new List<Session>(1);
            readonly ReaderWriterLockSlim _sessionsLock = new ReaderWriterLockSlim();

            BinaryNumber _maskedPeerUserId;

            #endregion

            #region constructor

            internal Peer(MeshNetwork network, BinaryNumber peerUserId, string peerDisplayName)
            {
                _network = network;
                _peerUserId = peerUserId;

                _isSelfPeer = _network._userId.Equals(_peerUserId);

                if (!_isSelfPeer)
                    _profile = new MeshNetworkPacketProfile(DateTime.MinValue, peerDisplayName, MeshProfileStatus.None, null);
            }

            #endregion

            #region IDisposable

            bool _isDisposing = false;
            bool _disposed = false;

            public void Dispose()
            {
                if (!_disposed)
                {
                    _isDisposing = true;

                    _sessionsLock.EnterWriteLock();
                    try
                    {
                        foreach (Session session in _sessions)
                            session.Dispose();

                        _sessions.Clear();
                    }
                    finally
                    {
                        _sessionsLock.ExitWriteLock();
                    }

                    _sessionsLock.Dispose();

                    _disposed = true;
                }
            }

            #endregion

            #region private event functions

            private void RaiseEventStateChanged()
            {
                _network._connectionManager.Node.SynchronizationContext.Send(delegate (object state)
                {
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }, null);
            }

            internal void RaiseEventProfileChanged()
            {
                _network._connectionManager.Node.SynchronizationContext.Send(delegate (object state)
                {
                    ProfileChanged?.Invoke(this, EventArgs.Empty);
                }, null);
            }

            private void RaiseEventConnectivityStatusChanged()
            {
                _network._connectionManager.Node.SynchronizationContext.Send(delegate (object state)
                {
                    ConnectivityStatusChanged?.Invoke(this, EventArgs.Empty);
                }, null);
            }

            #endregion

            #region internal/private

            internal void SendMessage(byte[] data, int offset, int count)
            {
                if (count > MAX_MESSAGE_SIZE)
                    throw new IOException("MeshNetwork message data size cannot exceed " + MAX_MESSAGE_SIZE + " bytes.");

                _sessionsLock.EnterReadLock();
                try
                {
                    foreach (Session session in _sessions)
                    {
                        session.SendMessage(data, offset, count);
                    }
                }
                finally
                {
                    _sessionsLock.ExitReadLock();
                }
            }

            internal void AddSession(SecureChannelStream channel, Connection connection)
            {
                Session session;

                _sessionsLock.EnterWriteLock();
                try
                {
                    session = new Session(this, channel, connection);
                    _sessions.Add(session);
                }
                finally
                {
                    _sessionsLock.ExitWriteLock();
                }

                if (!_isOnline)
                {
                    _isOnline = true;
                    RaiseEventStateChanged(); //notify UI that peer is online
                }

                //send profile & image to this session
                MeshNode node = _network._connectionManager.Node;
                session.SendMessage(new MeshNetworkPacketProfile(node.ProfileDateModified, node.ProfileDisplayName, node.ProfileStatus, node.ProfileStatusMessage));
                session.SendMessage(new MeshNetworkPacketProfileDisplayImage(node.ProfileDisplayImageDateModified, node.ProfileDisplayImage));

                //peer exchange
                _network.UpdateConnectivityStatus();
                _network.DoPeerExchange();

                switch (_network.Type)
                {
                    case MeshNetworkType.Private:
                        ReSendUndeliveredMessages(session); //feature only for private chat. since, group chat can have multiple offline users, sending undelivered messages will create partial & confusing conversation for the one who comes online later.
                        break;

                    case MeshNetworkType.Group:
                        session.SendMessage(new MeshNetworkPacketGroupDisplayImage(_network._groupDisplayImageDateModified, _network._groupDisplayImage)); //group image feature
                        break;
                }
            }

            private void RemoveSession(Session session)
            {
                if (!_isDisposing)
                {
                    //remove this session from peer
                    _sessionsLock.EnterWriteLock();
                    try
                    {
                        _sessions.Remove(session);

                        _isOnline = (_sessions.Count > 0);
                    }
                    finally
                    {
                        _sessionsLock.ExitWriteLock();
                    }

                    if (!_isOnline)
                    {
                        _connectivityStatus = PeerConnectivityStatus.NoNetwork;

                        RaiseEventStateChanged(); //notify UI that peer is offline
                        RaiseEventConnectivityStatusChanged(); //connectivity status changed
                    }

                    //peer exchange
                    _network.UpdateConnectivityStatus();
                    _network.DoPeerExchange();
                }
            }

            internal void Disconnect()
            {
                _sessionsLock.EnterReadLock();
                try
                {
                    foreach (Session session in _sessions)
                        session.Disconnect();
                }
                finally
                {
                    _sessionsLock.ExitReadLock();
                }
            }

            internal MeshNetworkPeerInfo GetPeerInfo()
            {
                List<EndPoint> peerEPList = new List<EndPoint>();

                _sessionsLock.EnterReadLock();
                try
                {
                    foreach (Session session in _sessions)
                        peerEPList.Add(session.RemotePeerEP);
                }
                finally
                {
                    _sessionsLock.ExitReadLock();
                }

                return new MeshNetworkPeerInfo(_peerUserId, _profile.ProfileDisplayName, peerEPList.ToArray());
            }

            internal List<MeshNetworkPeerInfo> GetConnectedPeerList()
            {
                List<MeshNetworkPeerInfo> connectedPeerList = new List<MeshNetworkPeerInfo>();

                if (_isSelfPeer)
                {
                    //add connected peer info from this mesh network for self

                    if (_network._type == MeshNetworkType.Private)
                    {
                        connectedPeerList.Add(this.GetPeerInfo());

                        if (_network._otherPeer._isOnline)
                            connectedPeerList.Add(_network._otherPeer.GetPeerInfo());
                    }
                    else
                    {
                        _network._peersLock.EnterReadLock();
                        try
                        {
                            foreach (KeyValuePair<BinaryNumber, Peer> peer in _network._peers)
                            {
                                if (peer.Value.IsOnline)
                                    connectedPeerList.Add(peer.Value.GetPeerInfo());
                            }
                        }
                        finally
                        {
                            _network._peersLock.ExitReadLock();
                        }
                    }
                }

                //note: self peer may have sessions from another device too and from this network

                //add peer info from sessions
                _sessionsLock.EnterReadLock();
                try
                {
                    foreach (Session session in _sessions)
                    {
                        foreach (MeshNetworkPeerInfo peerInfo in session.GetConnectedPeerList())
                        {
                            if (!connectedPeerList.Contains(peerInfo))
                                connectedPeerList.Add(peerInfo);
                        }
                    }
                }
                finally
                {
                    _sessionsLock.ExitReadLock();
                }

                //keep internal copy for later connectivity status check call and UI
                _connectedPeerList = connectedPeerList;

                return connectedPeerList;
            }

            internal void UpdateConnectivityStatus(List<MeshNetworkPeerInfo> uniquePeerInfoList)
            {
                PeerConnectivityStatus oldStatus = _connectivityStatus;

                List<MeshNetworkPeerInfo> connectedPeerList = _connectedPeerList;
                List<MeshNetworkPeerInfo> disconnectedPeerList = new List<MeshNetworkPeerInfo>();

                if (connectedPeerList != null)
                {
                    foreach (MeshNetworkPeerInfo checkEP in connectedPeerList)
                    {
                        if (!uniquePeerInfoList.Contains(checkEP))
                            disconnectedPeerList.Add(checkEP);
                    }

                    //remove self from the disconnected list
                    _disconnectedPeerList.Remove(new MeshNetworkPeerInfo(_peerUserId, new IPEndPoint(IPAddress.Any, 0))); //new object with just peer userId would be enough to remove it from list due to PeerInfo.Equals()
                }

                if (disconnectedPeerList.Count > 0)
                    _connectivityStatus = PeerConnectivityStatus.PartialMeshNetwork;
                else
                    _connectivityStatus = PeerConnectivityStatus.FullMeshNetwork;

                //keep a copy for UI
                _disconnectedPeerList = disconnectedPeerList;

                if (oldStatus != _connectivityStatus)
                    RaiseEventConnectivityStatusChanged();
            }

            private void ReSendUndeliveredMessages(Session session)
            {
                List<MessageItem> undeliveredMessages = new List<MessageItem>(10);
                BinaryNumber selfUserId = _network._selfPeer._peerUserId;

                for (int i = _network._store.GetMessageCount() - 1; i > -1; i--)
                {
                    MessageItem msg = new MessageItem(_network._store, i);

                    if ((msg.Type == MessageType.TextMessage) && msg.SenderUserId.Equals(selfUserId))
                    {
                        if (msg.GetDeliveryStatus() == MessageDeliveryStatus.Undelivered)
                            undeliveredMessages.Add(msg);
                        else
                            break;
                    }
                }

                for (int i = undeliveredMessages.Count - 1; i > -1; i--)
                {
                    session.SendMessage(undeliveredMessages[i].GetMeshNetworkPacket());
                }
            }

            private void DoSendGroupImage(Session session)
            {
                session.SendMessage(new MeshNetworkPacketGroupDisplayImage(_network._groupDisplayImageDateModified, _network._groupDisplayImage));
            }

            #endregion

            #region public

            public void ReceiveFileAttachment(int messageNumber, string filePath)
            {
                Thread t = new Thread(delegate (object state)
                {
                    try
                    {
                        Session[] sessions;

                        _sessionsLock.EnterReadLock();
                        try
                        {
                            sessions = _sessions.ToArray();
                        }
                        finally
                        {
                            _sessionsLock.ExitReadLock();
                        }

                        using (FileStream fS = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            long fileOffset = fS.Length;
                            fS.Position = fileOffset;

                            foreach (Session session in sessions)
                            {
                                if (session.ReceiveFileTo(messageNumber, fileOffset, fS))
                                {
                                    //file transfer success
                                    return;
                                }
                            }

                            //file transfer failed
                        }
                    }
                    catch
                    {

                    }
                });

                t.IsBackground = true;
                t.Start();
            }

            public override string ToString()
            {
                return this.ProfileDisplayName;
            }

            #endregion

            #region properties

            public BinaryNumber PeerUserId
            { get { return _peerUserId; } }

            public BinaryNumber MaskedPeerUserId
            {
                get
                {
                    if (_maskedPeerUserId == null)
                        _maskedPeerUserId = GetMaskedUserId(_peerUserId);

                    return _maskedPeerUserId;
                }
            }

            public MeshNetwork Network
            { get { return _network; } }

            public string ProfileDisplayName
            {
                get
                {
                    if (_isSelfPeer)
                        return _network._connectionManager.Node.ProfileDisplayName;
                    else
                        return _profile.ProfileDisplayName;
                }
            }

            public byte[] ProfileDisplayImage
            {
                get
                {
                    if (_isSelfPeer)
                        return _network._connectionManager.Node.ProfileDisplayImage;
                    else if (_profileImage != null)
                        return _profileImage.ProfileDisplayImage;
                    else
                        return null;
                }
            }

            public MeshProfileStatus ProfileStatus
            {
                get
                {
                    if (_isSelfPeer)
                        return _network._connectionManager.Node.ProfileStatus;
                    else
                        return _profile.ProfileStatus;
                }
            }

            public string ProfileStatusMessage
            {
                get
                {
                    if (_isSelfPeer)
                        return _network._connectionManager.Node.ProfileStatusMessage;
                    else
                        return _profile.ProfileStatusMessage;
                }
            }

            public bool IsOnline
            {
                get
                {
                    if (_isSelfPeer)
                        return true;

                    return _isOnline;
                }
            }

            public bool IsSelfPeer
            { get { return _isSelfPeer; } }

            public PeerConnectivityStatus ConnectivityStatus
            { get { return _connectivityStatus; } }

            public MeshNetworkPeerInfo[] ConnectedWith
            {
                get
                {
                    List<MeshNetworkPeerInfo> connectedPeerList = _connectedPeerList;

                    if (connectedPeerList == null)
                        return new MeshNetworkPeerInfo[] { };

                    return connectedPeerList.ToArray();
                }
            }

            public MeshNetworkPeerInfo[] NotConnectedWith
            {
                get
                {
                    List<MeshNetworkPeerInfo> disconnectedPeerList = _disconnectedPeerList;

                    if (disconnectedPeerList == null)
                        return new MeshNetworkPeerInfo[] { };

                    return disconnectedPeerList.ToArray();
                }
            }

            public SecureChannelCipherSuite CipherSuite
            {
                get
                {
                    _sessionsLock.EnterReadLock();
                    try
                    {
                        if (_sessions.Count > 0)
                            return _sessions[0].CipherSuite;
                        else
                            return SecureChannelCipherSuite.None;
                    }
                    finally
                    {
                        _sessionsLock.ExitReadLock();
                    }
                }
            }

            #endregion

            private class Session : IDisposable
            {
                #region variables

                readonly Peer _peer;
                readonly SecureChannelStream _channel;
                readonly Connection _connection;

                readonly Thread _readThread;

                MeshNetworkPacketPeerExchange _peerExchange; //saved info for getting connected peers for connectivity status

                readonly Dictionary<ushort, DataStream> _dataStreams = new Dictionary<ushort, DataStream>();
                ushort _lastPort = 0;

                #endregion

                #region constructor

                public Session(Peer peer, SecureChannelStream channel, Connection connection)
                {
                    _peer = peer;
                    _channel = channel;
                    _connection = connection;

                    //client will use odd port & server will use even port to avoid conflicts
                    if (_channel is SecureChannelClientStream)
                        _lastPort = 1;

                    //start read thread
                    _readThread = new Thread(ReadMessageAsync);
                    _readThread.IsBackground = true;
                    _readThread.Start();
                }

                #endregion

                #region IDisposable

                bool _isDisposing = false;
                bool _disposed = false;

                public void Dispose()
                {
                    lock (this)
                    {
                        if (!_disposed)
                        {
                            _isDisposing = true;

                            //close all data streams
                            lock (_dataStreams)
                            {
                                foreach (KeyValuePair<ushort, DataStream> dataStream in _dataStreams)
                                    dataStream.Value.Dispose();

                                _dataStreams.Clear();
                            }

                            //close base secure channel
                            try
                            {
                                _channel.Dispose();
                            }
                            catch
                            { }

                            //remove session
                            _peer.RemoveSession(this);

                            _disposed = true;
                        }
                    }
                }

                #endregion

                #region private

                private void WriteDataPacket(ushort port, byte[] data, int offset, int count)
                {
                    Monitor.Enter(_channel);
                    try
                    {
                        _channel.Write(BitConverter.GetBytes(port), 0, 2); //port
                        _channel.Write(BitConverter.GetBytes(Convert.ToUInt16(count)), 0, 2); //data length
                        _channel.Write(data, offset, count);
                        _channel.Flush();
                    }
                    catch
                    { }
                    finally
                    {
                        Monitor.Exit(_channel);
                    }
                }

                private void ReadMessageAsync(object state)
                {
                    try
                    {
                        BinaryReader bR = new BinaryReader(_channel);
                        ushort port;
                        ushort length;

                        while (true)
                        {
                            port = bR.ReadUInt16();

                            if (port == 0)
                            {
                                MeshNetworkPacket packet = MeshNetworkPacket.Parse(bR);

                                switch (packet.Type)
                                {
                                    case MeshNetworkPacketType.PingRequest:
                                        SendMessage(new MeshNetworkPacket(MeshNetworkPacketType.PingResponse));
                                        break;

                                    case MeshNetworkPacketType.PingResponse:
                                        //do nothing
                                        break;

                                    case MeshNetworkPacketType.PeerExchange:
                                        MeshNetworkPacketPeerExchange peerExchange = packet as MeshNetworkPacketPeerExchange;

                                        _peerExchange = peerExchange;
                                        _peer._network.UpdateConnectivityStatus();

                                        foreach (MeshNetworkPeerInfo peerInfo in peerExchange.Peers)
                                        {
                                            foreach (IPEndPoint peerEP in peerInfo.PeerEPs)
                                                _peer._network.BeginMakeConnection(peerEP, _connection);
                                        }

                                        break;

                                    case MeshNetworkPacketType.LocalNetworkOnly:
                                        MeshNetworkPacketLocalNetworkOnly localOnly = packet as MeshNetworkPacketLocalNetworkOnly;

                                        if (localOnly.LocalNetworkOnlyDateModified > _peer._network._localNetworkOnlyDateModified)
                                        {
                                            _peer._network._localNetworkOnly = localOnly.LocalNetworkOnly;

                                            string infoText;

                                            if (_peer._network._localNetworkOnly)
                                                infoText = "Mesh group network updated to work only on local LAN networks.";
                                            else
                                                infoText = "Mesh group network updated to work on Internet and local LAN networks.";

                                            MessageItem msg = new MessageItem(DateTime.UtcNow, _peer._peerUserId, null, MessageType.Info, infoText, null, null, 0, null);
                                            msg.WriteTo(_peer._network._store);

                                            _peer._network.RaiseEventMessageReceived(_peer, msg);
                                        }

                                        break;

                                    case MeshNetworkPacketType.Profile:
                                        MeshNetworkPacketProfile profile = packet as MeshNetworkPacketProfile;

                                        if (_peer._isSelfPeer)
                                        {
                                            MeshNode node = _peer._network._connectionManager.Node;

                                            if (profile.ProfileDateModified > node.ProfileDateModified)
                                            {
                                                node.UpdateProfileWithoutTriggerUpdate(profile.ProfileDateModified, profile.ProfileDisplayName, profile.ProfileStatus, profile.ProfileStatusMessage);

                                                _peer.RaiseEventProfileChanged();
                                            }
                                        }
                                        else
                                        {
                                            if (profile.ProfileDateModified > _peer._profile.ProfileDateModified)
                                            {
                                                _peer._profile = profile;

                                                _peer.RaiseEventProfileChanged();
                                            }
                                        }

                                        break;

                                    case MeshNetworkPacketType.ProfileDisplayImage:
                                        MeshNetworkPacketProfileDisplayImage profileImage = packet as MeshNetworkPacketProfileDisplayImage;

                                        if (_peer._isSelfPeer)
                                        {
                                            MeshNode node = _peer._network._connectionManager.Node;

                                            if (profileImage.ProfileDisplayImageDateModified > node.ProfileDisplayImageDateModified)
                                            {
                                                node.UpdateProfileDisplayImageWithoutTriggerUpdate(profileImage.ProfileDisplayImageDateModified, profileImage.ProfileDisplayImage);

                                                _peer.RaiseEventProfileChanged();
                                            }
                                        }
                                        else
                                        {
                                            if ((_peer._profileImage == null) || (profileImage.ProfileDisplayImageDateModified > _peer._profileImage.ProfileDisplayImageDateModified))
                                            {
                                                _peer._profileImage = profileImage;

                                                _peer.RaiseEventProfileChanged();
                                            }
                                        }

                                        break;

                                    case MeshNetworkPacketType.GroupDisplayImage:
                                        MeshNetworkPacketGroupDisplayImage groupImage = packet as MeshNetworkPacketGroupDisplayImage;

                                        if (groupImage.GroupDisplayImageDateModified > _peer._network._groupDisplayImageDateModified)
                                        {
                                            _peer._network._groupDisplayImage = groupImage.GroupDisplayImage;
                                            _peer._network._groupDisplayImageDateModified = groupImage.GroupDisplayImageDateModified;

                                            _peer._network.RaiseEventGroupImageChanged(_peer);

                                            MessageItem msg = new MessageItem(DateTime.UtcNow, _peer._peerUserId, null, MessageType.Info, "Group display image was updated.", null, null, 0, null);
                                            msg.WriteTo(_peer._network._store);

                                            _peer._network.RaiseEventMessageReceived(_peer, msg);
                                        }

                                        break;

                                    case MeshNetworkPacketType.GroupLockNetwork:
                                        MeshNetworkPacketGroupLockNetwork groupLock = packet as MeshNetworkPacketGroupLockNetwork;

                                        if (groupLock.GroupLockNetworkDateModified > _peer._network._groupLockNetworkDateModified)
                                        {
                                            _peer._network._groupLockNetwork = groupLock.GroupLockNetwork;

                                            string infoText;

                                            if (_peer._network._groupLockNetwork)
                                                infoText = "Mesh group network was locked.";
                                            else
                                                infoText = "Mesh group network was unlocked.";

                                            MessageItem msg = new MessageItem(DateTime.UtcNow, _peer._peerUserId, null, MessageType.Info, infoText, null, null, 0, null);
                                            msg.WriteTo(_peer._network._store);

                                            _peer._network.RaiseEventMessageReceived(_peer, msg);
                                        }
                                        break;

                                    case MeshNetworkPacketType.MessageTypingNotification:
                                        _peer._network.RaiseEventPeerTyping(_peer);
                                        break;

                                    case MeshNetworkPacketType.Message:
                                        {
                                            MeshNetworkPacketMessage message = packet as MeshNetworkPacketMessage;

                                            MessageItem msg = new MessageItem(_peer._peerUserId, message);
                                            msg.WriteTo(_peer._network._store);

                                            _peer._network.RaiseEventMessageReceived(_peer, msg);

                                            //send delivery notification
                                            SendMessage(new MeshNetworkPacketMessageDeliveryNotification(message.MessageNumber));
                                        }
                                        break;

                                    case MeshNetworkPacketType.MessageDeliveryNotification:
                                        {
                                            MeshNetworkPacketMessageDeliveryNotification notification = packet as MeshNetworkPacketMessageDeliveryNotification;

                                            MessageItem msg;

                                            lock (_peer._network._store) //lock to avoid race condition in a group chat. this will prevent message data from getting overwritten.
                                            {
                                                //read existing message from store
                                                msg = new MessageItem(_peer._network._store, notification.MessageNumber);

                                                foreach (MessageRecipient rcpt in msg.Recipients)
                                                {
                                                    if (rcpt.UserId.Equals(_peer._peerUserId))
                                                    {
                                                        rcpt.SetDeliveredStatus();
                                                        break;
                                                    }
                                                }

                                                //update message to store
                                                msg.WriteTo(_peer._network._store);
                                            }

                                            _peer._network.RaiseEventMessageDeliveryNotification(_peer, msg);
                                        }
                                        break;

                                    case MeshNetworkPacketType.FileRequest:
                                        {
                                            MeshNetworkPacketFileRequest fileRequest = packet as MeshNetworkPacketFileRequest;

                                            ThreadPool.QueueUserWorkItem(delegate (object state2)
                                            {
                                                try
                                                {
                                                    //open data port
                                                    using (DataStream dS = OpenDataStream(fileRequest.DataPort))
                                                    {
                                                        //read existing message from store
                                                        MessageItem msg = new MessageItem(_peer._network._store, fileRequest.MessageNumber);

                                                        if (msg.Type == MessageType.FileAttachment)
                                                        {
                                                            //open local file stream
                                                            using (FileStream fS = new FileStream(msg.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                                            {
                                                                //set file position to allow pause/resume transfer
                                                                fS.Position = fileRequest.FileOffset;
                                                                fS.CopyTo(dS);
                                                            }
                                                        }
                                                    }
                                                }
                                                catch
                                                { }
                                            });
                                        }
                                        break;

                                    default:
                                        //do nothing
                                        break;
                                }
                            }
                            else
                            {
                                length = bR.ReadUInt16();

                                DataStream stream = null;

                                lock (_dataStreams)
                                {
                                    stream = _dataStreams[port];
                                }

                                stream.FeedReadBuffer(_channel, length, 30000);
                            }
                        }
                    }
                    catch (SecureChannelException ex)
                    {
                        _peer._network.RaiseEventSecureChannelFailed(ex);
                        Dispose();
                    }
                    catch (EndOfStreamException)
                    {
                        //gracefull secure channel disconnection done
                        Dispose();
                    }
                    catch
                    {
                        Dispose();

                        //try reconnection due to unexpected channel closure (mostly read timed out exception)
                        _peer._network.BeginMakeConnection(_channel.RemotePeerEP);
                    }
                }

                private DataStream OpenDataStream(ushort port = 0)
                {
                    lock (_dataStreams)
                    {
                        if (port == 0)
                        {
                            do
                            {
                                _lastPort += 2;

                                if (_lastPort > (ushort.MaxValue - 3))
                                {
                                    if (_channel is SecureChannelClientStream)
                                        _lastPort = 1;
                                    else
                                        _lastPort = 0;

                                    continue;
                                }
                            }
                            while (_dataStreams.ContainsKey(_lastPort));

                            port = _lastPort;
                        }
                        else if (_dataStreams.ContainsKey(port))
                        {
                            throw new ArgumentException("Data port already in use.");
                        }

                        DataStream stream = new DataStream(this, port);
                        _dataStreams.Add(port, stream);

                        return stream;
                    }
                }

                #endregion

                #region public

                public void SendMessage(byte[] data, int offset, int count)
                {
                    WriteDataPacket(0, data, offset, count);
                }

                public void SendMessage(MeshNetworkPacket message)
                {
                    using (MemoryStream mS = new MemoryStream())
                    {
                        message.WriteTo(new BinaryWriter(mS));

                        byte[] buffer = mS.ToArray();
                        WriteDataPacket(0, buffer, 0, buffer.Length);
                    }
                }

                public bool ReceiveFileTo(int messageNumber, long fileOffset, Stream s)
                {
                    //open data port
                    using (DataStream dS = OpenDataStream())
                    {
                        //send file request
                        SendMessage(new MeshNetworkPacketFileRequest(messageNumber, fileOffset, dS.Port));

                        //read first byte for EOF test
                        int firstByte = dS.ReadByte();
                        if (firstByte < 0)
                            return false; //remote peer disconnected data stream; request failed

                        s.WriteByte((byte)firstByte); //write first byte

                        //start copying rest of data
                        dS.CopyTo(s);

                        return true; //success
                    }
                }

                public void Disconnect()
                {
                    try
                    {
                        _channel.Dispose();
                    }
                    catch
                    { }
                }

                public ICollection<MeshNetworkPeerInfo> GetConnectedPeerList()
                {
                    MeshNetworkPacketPeerExchange peerExchange = _peerExchange;
                    if (peerExchange == null)
                        return new MeshNetworkPeerInfo[] { };

                    return peerExchange.Peers;
                }

                #endregion

                #region properties

                public SecureChannelCipherSuite CipherSuite
                { get { return _channel.SelectedCipher; } }

                public EndPoint RemotePeerEP
                { get { return _channel.RemotePeerEP; } }

                #endregion

                private class DataStream : Stream
                {
                    #region variables

                    const int DATA_READ_TIMEOUT = 60000;
                    const int DATA_WRITE_TIMEOUT = 30000; //dummy

                    readonly Session _session;
                    readonly ushort _port;

                    readonly byte[] _readBuffer = new byte[DATA_STREAM_BUFFER_SIZE];
                    int _readBufferOffset;
                    int _readBufferCount;

                    int _readTimeout = DATA_READ_TIMEOUT;
                    int _writeTimeout = DATA_WRITE_TIMEOUT;

                    #endregion

                    #region constructor

                    public DataStream(Session session, ushort port)
                    {
                        _session = session;
                        _port = port;
                    }

                    #endregion

                    #region IDisposable

                    bool _disposed = false;

                    protected override void Dispose(bool disposing)
                    {
                        lock (this)
                        {
                            if (!_disposed)
                            {
                                try
                                {
                                    _session.WriteDataPacket(_port, new byte[] { }, 0, 0);
                                }
                                catch
                                { }

                                if (!_session._isDisposing)
                                {
                                    lock (_session._dataStreams)
                                    {
                                        _session._dataStreams.Remove(_port);
                                    }
                                }

                                Monitor.PulseAll(this);

                                _disposed = true;
                            }
                        }
                    }

                    #endregion

                    #region stream support

                    public override bool CanRead
                    {
                        get { return true; }
                    }

                    public override bool CanSeek
                    {
                        get { return false; }
                    }

                    public override bool CanWrite
                    {
                        get { return true; }
                    }

                    public override bool CanTimeout
                    {
                        get { return true; }
                    }

                    public override int ReadTimeout
                    {
                        get { return _readTimeout; }
                        set { _readTimeout = value; }
                    }

                    public override int WriteTimeout
                    {
                        get { return _writeTimeout; }
                        set { _writeTimeout = value; }
                    }

                    public override void Flush()
                    {
                        //do nothing
                    }

                    public override long Length
                    {
                        get { throw new NotSupportedException("DataStream stream does not support seeking."); }
                    }

                    public override long Position
                    {
                        get
                        {
                            throw new NotSupportedException("DataStream stream does not support seeking.");
                        }
                        set
                        {
                            throw new NotSupportedException("DataStream stream does not support seeking.");
                        }
                    }

                    public override long Seek(long offset, SeekOrigin origin)
                    {
                        throw new NotSupportedException("DataStream stream does not support seeking.");
                    }

                    public override void SetLength(long value)
                    {
                        throw new NotSupportedException("DataStream stream does not support seeking.");
                    }

                    public override int Read(byte[] buffer, int offset, int count)
                    {
                        if (count < 1)
                            throw new ArgumentOutOfRangeException("Count must be atleast 1 byte.");

                        lock (this)
                        {
                            if (_readBufferCount < 1)
                            {
                                if (_disposed)
                                    return 0;

                                if (!Monitor.Wait(this, _readTimeout))
                                    throw new IOException("Read timed out.");

                                if (_readBufferCount < 1)
                                    return 0;
                            }

                            int bytesToCopy = count;

                            if (bytesToCopy > _readBufferCount)
                                bytesToCopy = _readBufferCount;

                            Buffer.BlockCopy(_readBuffer, _readBufferOffset, buffer, offset, bytesToCopy);

                            _readBufferOffset += bytesToCopy;
                            _readBufferCount -= bytesToCopy;

                            if (_readBufferCount < 1)
                                Monitor.Pulse(this);

                            return bytesToCopy;
                        }
                    }

                    public override void Write(byte[] buffer, int offset, int count)
                    {
                        if (_disposed)
                            throw new ObjectDisposedException("DataStream");

                        _session.WriteDataPacket(_port, buffer, offset, count);
                    }

                    #endregion

                    #region private

                    public void FeedReadBuffer(Stream s, int length, int timeout)
                    {
                        if (length < 1)
                        {
                            Dispose();
                            return;
                        }

                        int readCount = _readBuffer.Length;

                        while (length > 0)
                        {
                            lock (this)
                            {
                                if (_disposed)
                                {
                                    OffsetStream.StreamCopy(new OffsetStream(s, 0, length), Stream.Null, 1024); //remove unread data from the source stream
                                    throw new ObjectDisposedException("DataStream");
                                }

                                if (_readBufferCount > 0)
                                {
                                    if (!Monitor.Wait(this, timeout))
                                    {
                                        OffsetStream.StreamCopy(new OffsetStream(s, 0, length), Stream.Null, 1024); //remove unread data from the source stream
                                        throw new IOException("DataStream FeedReadBuffer timed out.");
                                    }

                                    if (_readBufferCount > 0)
                                    {
                                        OffsetStream.StreamCopy(new OffsetStream(s, 0, length), Stream.Null, 1024); //remove unread data from the source stream
                                        throw new IOException("DataStream FeedReadBuffer failed. Buffer not empty.");
                                    }
                                }

                                if (length < readCount)
                                    readCount = length;

                                s.ReadBytes(_readBuffer, 0, readCount);
                                _readBufferOffset = 0;
                                _readBufferCount = readCount;
                                length -= readCount;

                                Monitor.Pulse(this);
                            }
                        }
                    }

                    #endregion

                    #region properties

                    public ushort Port
                    { get { return _port; } }

                    #endregion
                }
            }
        }
    }
}