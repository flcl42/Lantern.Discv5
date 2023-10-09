using System.Collections.Concurrent;
using System.Diagnostics;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Message;

public class RequestManager : IRequestManager
{
    private readonly ConcurrentDictionary<byte[], PendingRequest> _pendingRequests;
    private readonly ConcurrentDictionary<byte[], CachedHandshakeInteraction> _cachedHandshakeInteractions;
    private readonly ConcurrentDictionary<byte[], CachedRequest> _cachedRequests;
    private readonly IRoutingTable _routingTable;
    private readonly ILogger<RequestManager> _logger;
    private readonly TableOptions _tableOptions;
    private readonly ConnectionOptions _connectionOptions;
    private readonly ICancellationTokenSourceWrapper _cts;
    private readonly IGracefulTaskRunner _taskRunner;
    private Task _checkAllRequestsTask;

    public RequestManager(
        IRoutingTable routingTable, 
        ILoggerFactory loggerFactory, 
        ICancellationTokenSourceWrapper cts, 
        IGracefulTaskRunner taskRunner, 
        TableOptions tableOptions, 
        ConnectionOptions connectionOptions)
    {
        _pendingRequests = new ConcurrentDictionary<byte[], PendingRequest>(ByteArrayEqualityComparer.Instance);
        _cachedHandshakeInteractions = new ConcurrentDictionary<byte[], CachedHandshakeInteraction>(ByteArrayEqualityComparer.Instance);
        _cachedRequests = new ConcurrentDictionary<byte[], CachedRequest>(ByteArrayEqualityComparer.Instance);
        _routingTable = routingTable;
        _logger = loggerFactory.CreateLogger<RequestManager>();
        _tableOptions = tableOptions;
        _connectionOptions = connectionOptions;
        _cts = cts;
        _taskRunner = taskRunner;
        _checkAllRequestsTask = Task.CompletedTask;
    }
    
    public int PendingRequestsCount => _pendingRequests.Count;
    
    public int CachedRequestsCount => _cachedRequests.Count;
    
    public int CachedHandshakeInteractionsCount => _cachedHandshakeInteractions.Count;
    
    public void StartRequestManager()
    {
        _logger.LogInformation("Starting RequestManagerAsync");
        _checkAllRequestsTask = _taskRunner.RunWithGracefulCancellationAsync(CheckAllRequests, "CheckAllRequests", _cts.GetToken());
    }

    public async Task StopRequestManagerAsync()
    {
        _logger.LogInformation("Stopping RequestManagerAsync");
        _cts.Cancel();

        await _checkAllRequestsTask.ConfigureAwait(false);
	
        if (_cts.IsCancellationRequested())
        {
            _logger.LogInformation("RequestManagerAsync was canceled gracefully");
        }
    }

    public bool AddPendingRequest(byte[] requestId, PendingRequest request)
    {
        var result = _pendingRequests.ContainsKey(requestId);
        
        _pendingRequests.AddOrUpdate(requestId, request, (_, _) => request);

        if (!result)
        {
            _routingTable.MarkNodeAsPending(request.NodeId);
            _logger.LogDebug("Added pending request with id {RequestId}", Convert.ToHexString(requestId));
        }

        return !result;
    }

    public bool AddCachedRequest(byte[] requestId, CachedRequest request)
    {
        var result = _cachedRequests.ContainsKey(requestId);
        _cachedRequests.AddOrUpdate(requestId, request, (_, _) => request);

        if (!result)
        {
            _routingTable.MarkNodeAsPending(request.NodeId);
            _logger.LogDebug("Added cached request with id {RequestId}", Convert.ToHexString(requestId));
        }

        return !result;
    }
    
    public void AddCachedHandshakeInteraction(byte[] packetNonce, byte[] destNodeId)
    { 
        
        if (_cachedHandshakeInteractions.Count >= 500)
        {
            // If we have more than 500 cached handshake interactions, remove 250 oldest ones
            var oldestInteractions = _cachedHandshakeInteractions.OrderBy(x => x.Value.ElapsedTime.Elapsed).Take(400).ToList();
            
            foreach (var interaction in oldestInteractions)
            {
                _cachedHandshakeInteractions.TryRemove(interaction.Key, out _);
            }
        }

        _cachedHandshakeInteractions.TryAdd(packetNonce, new CachedHandshakeInteraction(destNodeId));
    }
    
    public byte[]? GetCachedHandshakeInteraction(byte[] packetNonce)
    {
        _cachedHandshakeInteractions.TryRemove(packetNonce, out var destNodeId);
        
        if(destNodeId == null)
        {
            _logger.LogWarning("Failed to get dest node id from packet nonce. Ignoring WHOAREYOU request");
            return null;
        }

        return destNodeId.NodeId;
    }

    public bool ContainsCachedRequest(byte[] requestId)
    {
        return _cachedRequests.ContainsKey(requestId);
    }
    
    public PendingRequest? GetPendingRequest(byte[] requestId)
    {
        _pendingRequests.TryGetValue(requestId, out var request);
        return request;
    }
    
    public PendingRequest? GetPendingRequestByNodeId(byte[] nodeId)
    {
        return _pendingRequests.Values.FirstOrDefault(x => x.NodeId.SequenceEqual(nodeId));
    }

    public CachedRequest? GetCachedRequest(byte[] requestId)
    {
        _cachedRequests.TryGetValue(requestId, out var request);
        return request;
    }

    public PendingRequest? MarkRequestAsFulfilled(byte[] requestId)
    {
        if (!_pendingRequests.TryGetValue(requestId, out var request)) 
            return null;
        
        request.IsFulfilled = true;
        request.ResponsesCount++;
        
        return request;
    }

    public CachedRequest? MarkCachedRequestAsFulfilled(byte[] requestId)
    {
        _logger.LogDebug("Marking cached request as fulfilled with id {RequestId}", Convert.ToHexString(requestId));
        _cachedRequests.TryRemove(requestId, out var request);

        return request;
    }
    
    private async Task CheckAllRequests(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            CheckRequests();
            await Task.Delay(Math.Min(_connectionOptions.CheckPendingRequestsDelayMs, _connectionOptions.RemoveCompletedRequestsDelayMs), token);
            RemoveFulfilledRequests();
        }
    }

    // CheckRequests and RemoveFulfilledRequests can likely be merged into one method
    private void CheckRequests()
    {
        _logger.LogDebug("Checking for pending and cached requests");

        var currentPendingRequests = _pendingRequests.Values.ToList();
        var currentCachedRequests = _cachedRequests.Values.ToList();

        foreach (var pendingRequest in currentPendingRequests)
        {
            HandlePendingRequest(pendingRequest);
        }
        
        foreach (var cachedRequest in currentCachedRequests)
        {
            HandleCachedRequest(cachedRequest);
        }
    }
    
    private void RemoveFulfilledRequests()
    {
        _logger.LogDebug("Removing fulfilled requests");

        var completedTasks = _pendingRequests.Values
            .Where(x => x.IsFulfilled)
            .ToList();
        
        foreach (var task in completedTasks)
        {
            if (task.Message.MessageType == MessageType.FindNode)
            {
                if (task.ResponsesCount == task.MaxResponses)
                {
                    _pendingRequests.TryRemove(task.Message.RequestId, out _);
                }
            }
            else
            {
                _pendingRequests.TryRemove(task.Message.RequestId, out _);
            }
        }
    }

    private void HandlePendingRequest(PendingRequest request)
    {
        if (request.ElapsedTime.ElapsedMilliseconds <= _connectionOptions.RequestTimeoutMs) 
            return;
        
        _logger.LogDebug("Pending request timed out for node {NodeId}", Convert.ToHexString(request.NodeId));

        _pendingRequests.TryRemove(request.Message.RequestId, out _);
        
        var nodeEntry = _routingTable.GetNodeEntry(request.NodeId);

        if (nodeEntry == null) 
            return;
        
        if(nodeEntry.FailureCounter >= _tableOptions.MaxAllowedFailures)
        {
            _logger.LogDebug("Node {NodeId} has reached max retries. Marking as dead", Convert.ToHexString(request.NodeId));
           
        }
        else
        {
            _logger.LogDebug("Increasing failure counter for Node {NodeId}",Convert.ToHexString(request.NodeId));
            _routingTable.IncreaseFailureCounter(request.NodeId);
        }
    }

    private void HandleCachedRequest(CachedRequest request)
    {
        if (request.ElapsedTime.ElapsedMilliseconds <= _connectionOptions.RequestTimeoutMs) 
            return;
        
        _cachedRequests.TryRemove(request.NodeId, out _);  
        
        var nodeEntry = _routingTable.GetNodeEntry(request.NodeId);
            
        if (nodeEntry == null)
        {
            _logger.LogDebug("Node {NodeId} not found in routing table", Convert.ToHexString(request.NodeId));
            return;
        }

        _routingTable.MarkNodeAsDead(request.NodeId);
    }
}