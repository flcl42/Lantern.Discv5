using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.EnrFactory;
using Lantern.Discv5.Enr.IdentityScheme.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Message;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class OrdinaryPacketHandlerTests
{
    private Mock<IPacketProcessor> mockPacketProcessor;
    private Mock<ISessionManager> mockSessionManager;
    private Mock<IRoutingTable> mockRoutingTable;
    private Mock<IMessageResponder> mockMessageResponder;
    private Mock<IUdpConnection> mockUdpConnection;
    private Mock<IPacketBuilder> mockPacketBuilder;
    private Mock<ISessionMain> mockSessionMain;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<OrdinaryPacketHandler>> logger;
    
    [SetUp]
    public void Init()
    {
        mockPacketProcessor = new Mock<IPacketProcessor>();
        mockSessionManager = new Mock<ISessionManager>();
        mockRoutingTable = new Mock<IRoutingTable>();
        mockMessageResponder = new Mock<IMessageResponder>();
        mockUdpConnection = new Mock<IUdpConnection>();
        mockPacketBuilder = new Mock<IPacketBuilder>();
        mockSessionMain = new Mock<ISessionMain>();
        logger = new Mock<ILogger<OrdinaryPacketHandler>>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        logger
            .Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(), (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()));
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public void Test_PacketType_ShouldReturnOrdinaryType()
    {
        Assert.AreEqual(PacketType.Ordinary, new OrdinaryPacketHandler(mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object,mockLoggerFactory.Object).PacketType);
    }

    [Test]
    [TestCase("18.223.219.100", 9000)]
    public async Task Test_HandlePacket_ShouldSendWhoAreYouPacketWithoutEnr_WhenNodeEntryIsNull(string ip, int port)
    {
        // Test data
        var staticHeader = new StaticHeader("test", new byte[32], new byte[32], 0, new byte[32]);
        
        // Arrange
        mockPacketBuilder
            .Setup(x => x.BuildWhoAreYouPacketWithoutEnr(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new Tuple<byte[], StaticHeader>(new byte[32], staticHeader));
        mockPacketProcessor
            .Setup(x => x.GetStaticHeader(It.IsAny<byte[]>()))
            .Returns(new StaticHeader("test", new byte[32], new byte[32], 0, new byte[32]));
        mockUdpConnection
            .Setup(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.CompletedTask);
        mockSessionManager
            .Setup(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);

        var handler = new OrdinaryPacketHandler(mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, 
            mockPacketProcessor.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse(ip), port));

        // Act
        await handler.HandlePacket(fakeResult);
    
        // Assert
        mockPacketProcessor.Verify(x => x.GetStaticHeader(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetMaskingIv(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetEncryptedMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.GetNodeEntry(It.IsAny<byte[]>()), Times.Once);
        mockPacketBuilder.Verify(x => x.BuildWhoAreYouPacketWithoutEnr(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
    }

    [Test]
    [TestCase("18.223.219.100", 9000)]
    public async Task Test_HandlePacket_ShouldSendWhoAreYouPacket_WhenSessionIsNull(string ip, int port)
    {
        // Test data
        var enrRecord = new EnrRecordFactory().CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8");
        var ipEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        var staticHeader = new StaticHeader("test", new byte[32], new byte[32], 0, new byte[32]);
        var packetTuple = new Tuple<byte[], StaticHeader>(new byte[32], staticHeader);
        
        // Arrange
        mockPacketProcessor
            .Setup(x => x.GetStaticHeader(It.IsAny<byte[]>()))
            .Returns(staticHeader);
        mockRoutingTable
            .Setup(x => x.GetNodeEntry(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentitySchemeV4Verifier()));
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns((ISessionMain?)null);
        mockSessionManager
            .Setup(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockPacketBuilder
            .Setup(x => x.BuildWhoAreYouPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<EnrRecord>(), It.IsAny<byte[]>()))
            .Returns(packetTuple);
        mockUdpConnection
            .Setup(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.CompletedTask);

        var handler = new OrdinaryPacketHandler(mockSessionManager.Object, mockRoutingTable.Object, mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], ipEndpoint);
        
        // Act
        await handler.HandlePacket(fakeResult);
        
        // Assert
        mockPacketProcessor.Verify(x => x.GetStaticHeader(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetMaskingIv(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetEncryptedMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.GetNodeEntry(It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockPacketBuilder.Verify(x => x.BuildWhoAreYouPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<EnrRecord>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
    }

    [Test] 
    [TestCase("18.223.219.100", 9000)]
    public async Task Test_HandlePacket_ShouldSendWhoAreYouPacket_WhenDecryptedMessageIsNull(string ip, int port)
    {
        // Test data
        var enrRecord = new EnrRecordFactory().CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8");
        var ipEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        var staticHeader = new StaticHeader("test", new byte[32], new byte[32], 0, new byte[32]);
        var packetTuple = new Tuple<byte[], StaticHeader>(new byte[32], staticHeader);
        
        // Arrange
        mockPacketProcessor
            .Setup(x => x.GetStaticHeader(It.IsAny<byte[]>()))
            .Returns(staticHeader);
        mockRoutingTable
            .Setup(x => x.GetNodeEntry(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentitySchemeV4Verifier()));
        mockSessionMain
            .Setup(x => x.DecryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[]?)null);
        mockSessionMain
            .Setup(x => x.SetChallengeData(It.IsAny<byte[]>(), It.IsAny<byte[]>()));
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockSessionManager
            .Setup(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockPacketBuilder
            .Setup(x => x.BuildWhoAreYouPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<EnrRecord>(), It.IsAny<byte[]>()))
            .Returns(packetTuple);
        mockUdpConnection
            .Setup(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.CompletedTask);

        // Act
        var handler = new OrdinaryPacketHandler(mockSessionManager.Object, mockRoutingTable.Object, mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], ipEndpoint);
        await handler.HandlePacket(fakeResult);
        
        // Assert
        mockPacketProcessor.Verify(x => x.GetStaticHeader(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetMaskingIv(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetEncryptedMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.GetNodeEntry(It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockPacketBuilder.Verify(x => x.BuildWhoAreYouPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<EnrRecord>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.CreateSession(It.IsAny<SessionType>(), It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
    }

    [Test]
    [TestCase("18.223.219.100", 9000)]
    public async Task Test_HandlePacket_ShouldSendResponseToOrdinaryPacket_WhenReplyIsNotNull(string ip, int port)
    {
        // Test data
        var enrRecord = new EnrRecordFactory().CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8");
        var ipEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        var staticHeader = new StaticHeader("test", new byte[32], new byte[32], 0, new byte[32]);
        var packetTuple = new Tuple<byte[], StaticHeader>(new byte[32], staticHeader);
        
        // Arrange
        mockPacketProcessor
            .Setup(x => x.GetStaticHeader(It.IsAny<byte[]>()))
            .Returns(staticHeader);
        mockRoutingTable
            .Setup(x => x.GetNodeEntry(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentitySchemeV4Verifier()));
        mockSessionMain
            .Setup(x => x.DecryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new byte[32]);
        mockSessionMain
            .Setup(x => x.SetChallengeData(It.IsAny<byte[]>(), It.IsAny<byte[]>()));
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockPacketBuilder
            .Setup(x => x.BuildOrdinaryPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(packetTuple);
        mockUdpConnection
            .Setup(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.CompletedTask);

        // Act
        var handler = new OrdinaryPacketHandler(mockSessionManager.Object, mockRoutingTable.Object, mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], ipEndpoint);
        await handler.HandlePacket(fakeResult);
        
        // Assert
        mockPacketProcessor.Verify(x => x.GetStaticHeader(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetMaskingIv(It.IsAny<byte[]>()), Times.Once);
        mockPacketProcessor.Verify(x => x.GetEncryptedMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.GetNodeEntry(It.IsAny<byte[]>()), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockPacketBuilder.Verify(x => x.BuildOrdinaryPacket(It.IsAny<byte[]>(),It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
    }
}