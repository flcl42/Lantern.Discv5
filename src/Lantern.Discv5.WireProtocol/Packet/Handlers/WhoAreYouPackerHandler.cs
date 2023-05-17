using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Rlp;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Message;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public class WhoAreYouPacketHandler : PacketHandlerBase
{
    private readonly IIdentityManager _identityManager;
    private readonly ISessionManager _sessionManager;
    private readonly IRoutingTable _routingTable;
    private readonly IMessageRequester _messageRequester;
    private readonly IAesUtility _aesUtility;
    private readonly IPacketBuilder _packetBuilder;
    private readonly ILogger<WhoAreYouPacketHandler> _logger;

    public WhoAreYouPacketHandler(IIdentityManager identityManager, ISessionManager sessionManager, IRoutingTable routingTable, IMessageRequester messageRequester, IAesUtility aesUtility, IPacketBuilder packetBuilder, ILoggerFactory loggerFactory)
    {
        _identityManager = identityManager;
        _sessionManager = sessionManager;
        _routingTable = routingTable;
        _messageRequester = messageRequester;
        _aesUtility = aesUtility;
        _packetBuilder = packetBuilder;
        _logger = loggerFactory.CreateLogger<WhoAreYouPacketHandler>();
    }

    public override PacketType PacketType => PacketType.WhoAreYou;

    public override async Task HandlePacket(IUdpConnection connection, UdpReceiveResult returnedResult)
    {
        _logger.LogInformation("Received WHOAREYOU packet from {Address}", returnedResult.RemoteEndPoint.Address);
        var packet = new PacketProcessor(_identityManager, _aesUtility, returnedResult.Buffer);
        var destNodeId = _sessionManager.GetHandshakeInteraction(packet.StaticHeader.Nonce);
        
        if (destNodeId == null)
        {
            _logger.LogWarning("Failed to get dest node id from packet nonce");
            return;
        }
        
        var nodeEntry = _routingTable.GetNodeEntry(destNodeId);
        
        if(nodeEntry == null)
        {
            _logger.LogWarning("Failed to get node entry from the ENR table at node id: {NodeId}", Convert.ToHexString(destNodeId));
            return;
        }
        
        var session = GenerateOrUpdateSession(packet, destNodeId, returnedResult.RemoteEndPoint);
        var message = _messageRequester.ConstructMessage(MessageType.Ping, destNodeId);
        
        if(message == null)
        {
            _logger.LogWarning("Failed to construct PING message");
            return;
        }
        
        var idSignatureNew = session.GenerateIdSignature(destNodeId);
        var maskingIv = RandomUtility.GenerateMaskingIv(PacketConstants.MaskingIvSize);
        var handshakePacket = _packetBuilder.BuildHandshakePacket(idSignatureNew, session.EphemeralPublicKey, destNodeId, maskingIv, session.MessageCount);
        var encryptedMessage = session.EncryptMessageWithNewKeys(nodeEntry.Record, handshakePacket.Item2, _identityManager.NodeId, message, maskingIv);
        var finalPacket = ByteArrayUtils.JoinByteArrays(handshakePacket.Item1, encryptedMessage);
        
        await connection.SendAsync(finalPacket, returnedResult.RemoteEndPoint);
        _logger.LogInformation("Sent HANDSHAKE packet with encrypted message");
    }

    private SessionMain? GenerateOrUpdateSession(PacketProcessor packet, byte[] destNodeId, IPEndPoint destEndPoint)
    {
        var session = _sessionManager.GetSession(destNodeId, destEndPoint);

        if (session == null)
        {
            _logger.LogInformation("Creating new session with node: {Node}", destEndPoint);
            session = _sessionManager.CreateSession(SessionType.Initiator, destNodeId, destEndPoint);
        }

        if (session != null)
        {
            session.SetChallengeData(packet.MaskingIv, packet.StaticHeader.GetHeader());
            return session;
        }
        
        _logger.LogWarning("Failed to create or update session with node: {Node}", destEndPoint);
        return null;
    }
}