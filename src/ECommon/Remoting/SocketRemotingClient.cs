﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Components;
using ECommon.Extensions;
using ECommon.Logging;
using ECommon.Remoting.Exceptions;
using ECommon.Scheduling;
using ECommon.Socketing;
using ECommon.Socketing.BufferManagement;
using ECommon.Utilities;

namespace ECommon.Remoting
{
    public class SocketRemotingClient
    {
        private readonly byte[] TimeoutMessage = Encoding.UTF8.GetBytes("Remoting request timeout.");
        private readonly Dictionary<int, IResponseHandler> _responseHandlerDict;
        private readonly Dictionary<int, IRemotingServerMessageHandler> _remotingServerMessageHandlerDict;
        private readonly IList<IConnectionEventListener> _connectionEventListeners;
        private readonly ConcurrentDictionary<long, ResponseFuture> _responseFutureDict;
        private readonly BlockingCollection<byte[]> _replyMessageQueue;
        private readonly IScheduleService _scheduleService;
        private readonly IBufferPool _receiveDataBufferPool;
        private readonly ILogger _logger;
        private readonly SocketSetting _setting;
        private readonly byte[] HeartbeatMessage = new byte[0];
        private int _heartbeatFailedCount;

        private EndPoint _serverEndPoint;
        private EndPoint _localEndPoint;
        private ClientSocket _clientSocket;
        private int _reconnecting = 0;
        private bool _shutteddown = false;
        private bool _started = false;

        public bool IsConnected
        {
            get { return _clientSocket != null &&_clientSocket.IsConnected; }
        }
        public EndPoint LocalEndPoint
        {
            get { return _localEndPoint; }
        }
        public EndPoint ServerEndPoint
        {
            get { return _serverEndPoint; }
        }
        public ClientSocket ClientSocket
        {
            get { return _clientSocket; }
        }
        public IBufferPool BufferPool
        {
            get { return _receiveDataBufferPool; }
        }

        public SocketRemotingClient() : this(new IPEndPoint(IPAddress.Loopback, 5000)) { }
        public SocketRemotingClient(EndPoint serverEndPoint, SocketSetting setting = null, EndPoint localEndPoint = null)
        {
            Ensure.NotNull(serverEndPoint, "serverEndPoint");

            _serverEndPoint = serverEndPoint;
            _localEndPoint = localEndPoint;
            _setting = setting ?? new SocketSetting();
            _receiveDataBufferPool = new BufferPool(_setting.ReceiveDataBufferSize, _setting.ReceiveDataBufferPoolSize);
            _clientSocket = new ClientSocket(_serverEndPoint, _localEndPoint, _setting, _receiveDataBufferPool, HandleServerMessage);
            _responseFutureDict = new ConcurrentDictionary<long, ResponseFuture>();
            _replyMessageQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
            _responseHandlerDict = new Dictionary<int, IResponseHandler>();
            _remotingServerMessageHandlerDict = new Dictionary<int, IRemotingServerMessageHandler>();
            _connectionEventListeners = new List<IConnectionEventListener>();
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);

            RegisterConnectionEventListener(new ConnectionEventListener(this));
        }

        public SocketRemotingClient RegisterResponseHandler(int requestCode, IResponseHandler responseHandler)
        {
            if (requestCode == _setting.HeartbeatRequestCode)
            {
                throw new Exception(string.Format("Request code cannot be hearbeat request code: {0}", _setting.HeartbeatRequestCode));
            }
            _responseHandlerDict[requestCode] = responseHandler;
            return this;
        }
        public SocketRemotingClient RegisterRemotingServerMessageHandler(int messageCode, IRemotingServerMessageHandler messageHandler)
        {
            _remotingServerMessageHandlerDict[messageCode] = messageHandler;
            return this;
        }
        public SocketRemotingClient RegisterConnectionEventListener(IConnectionEventListener listener)
        {
            _connectionEventListeners.Add(listener);
            _clientSocket.RegisterConnectionEventListener(listener);
            return this;
        }
        public SocketRemotingClient Start()
        {
            if (_started) return this;

            StartClientSocket();
            StartScanTimeoutRequestTask();
            StartSendHeartbeatTask();
            _shutteddown = false;
            _started = true;
            return this;
        }
        public void Shutdown()
        {
            _shutteddown = true;
            StopReconnectServerTask();
            StopScanTimeoutRequestTask();
            ShutdownClientSocket();
        }

        public Task<RemotingResponse> InvokeAsync(RemotingRequest request, int timeoutMillis)
        {
            EnsureClientStatus();

            request.Type = RemotingRequestType.Async;
            var taskCompletionSource = new TaskCompletionSource<RemotingResponse>();
            var responseFuture = new ResponseFuture(request, timeoutMillis, taskCompletionSource);

            if (!_responseFutureDict.TryAdd(request.Sequence, responseFuture))
            {
                throw new ResponseFutureAddFailedException(request.Sequence);
            }

            _clientSocket.QueueMessage(RemotingUtil.BuildRequestMessage(request));

            return taskCompletionSource.Task;
        }
        public void InvokeWithCallback(RemotingRequest request)
        {
            EnsureClientStatus();

            request.Type = RemotingRequestType.Callback;
            _clientSocket.QueueMessage(RemotingUtil.BuildRequestMessage(request));
        }
        public void InvokeOneway(RemotingRequest request)
        {
            EnsureClientStatus();

            request.Type = RemotingRequestType.Oneway;
            _clientSocket.QueueMessage(RemotingUtil.BuildRequestMessage(request));
        }

        private void HandleServerMessage(ITcpConnection connection, byte[] message)
        {
            if (message == null) return;

            var remotingServerMessage = RemotingUtil.ParseRemotingServerMessage(message);

            if (remotingServerMessage.Type == RemotingServerMessageType.RemotingResponse)
            {
                HandleResponseMessage(connection, remotingServerMessage.Body);
            }
            else if (remotingServerMessage.Type == RemotingServerMessageType.ServerMessage)
            {
                HandleServerPushMessage(connection, remotingServerMessage);
            }
        }
        private void HandleResponseMessage(ITcpConnection connection, byte[] message)
        {
            if (message == null) return;

            var remotingResponse = RemotingUtil.ParseResponse(message);

            if (remotingResponse.RequestType == RemotingRequestType.Callback)
            {
                IResponseHandler responseHandler;
                if (_responseHandlerDict.TryGetValue(remotingResponse.RequestCode, out responseHandler))
                {
                    responseHandler.HandleResponse(remotingResponse);
                }
                else
                {
                    _logger.ErrorFormat("No response handler found for remoting response:{0}", remotingResponse);
                }
            }
            else if (remotingResponse.RequestType == RemotingRequestType.Async)
            {
                ResponseFuture responseFuture;
                if (_responseFutureDict.TryRemove(remotingResponse.RequestSequence, out responseFuture))
                {
                    if (responseFuture.SetResponse(remotingResponse))
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.DebugFormat("Remoting response back, request code:{0}, requect sequence:{1}, time spent:{2}", responseFuture.Request.Code, responseFuture.Request.Sequence, (DateTime.Now - responseFuture.BeginTime).TotalMilliseconds);
                        }
                    }
                    else
                    {
                        _logger.ErrorFormat("Set remoting response failed, response:" + remotingResponse);
                    }
                }
            }
        }
        private void HandleServerPushMessage(ITcpConnection connection, RemotingServerMessage message)
        {
            IRemotingServerMessageHandler messageHandler;
            if (_remotingServerMessageHandlerDict.TryGetValue(message.Code, out messageHandler))
            {
                messageHandler.HandleMessage(message);
            }
            else
            {
                _logger.ErrorFormat("No handler found for remoting server message:{0}", message);
            }
        }
        private void ScanTimeoutRequest()
        {
            var timeoutKeyList = new List<long>();
            foreach (var entry in _responseFutureDict)
            {
                if (entry.Value.IsTimeout())
                {
                    timeoutKeyList.Add(entry.Key);
                }
            }
            foreach (var key in timeoutKeyList)
            {
                ResponseFuture responseFuture;
                if (_responseFutureDict.TryRemove(key, out responseFuture))
                {
                    var request = responseFuture.Request;
                    responseFuture.SetResponse(new RemotingResponse(
                        request.Type,
                        request.Code,
                        request.Sequence,
                        request.CreatedTime,
                        0,
                        TimeoutMessage,
                        DateTime.Now,
                        request.Header,
                        null));
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Removed timeout request:{0}", responseFuture.Request);
                    }
                }
            }
        }
        private void ReconnectServer(bool ignoreConnected = false)
        {
            _logger.InfoFormat("Try to reconnect to server, address: {0}", _serverEndPoint);

            if (!ignoreConnected && _clientSocket.IsConnected) return;
            if (!EnterReconnecting()) return;

            try
            {
                _clientSocket.Shutdown();
                _clientSocket = new ClientSocket(_serverEndPoint, _localEndPoint, _setting, _receiveDataBufferPool, HandleServerMessage);
                foreach (var listener in _connectionEventListeners)
                {
                    _clientSocket.RegisterConnectionEventListener(listener);
                }
                _clientSocket.Start();
            }
            catch (Exception ex)
            {
                _logger.Error("Reconnect to server error.", ex);
            }
            finally
            {
                ExitReconnecting();
            }
        }
        private void StartClientSocket()
        {
            _clientSocket.Start();
        }
        private void ShutdownClientSocket()
        {
            _clientSocket.Shutdown();
        }
        private void StartScanTimeoutRequestTask()
        {
            _scheduleService.StartTask(string.Format("{0}.ScanTimeoutRequest", this.GetType().Name), ScanTimeoutRequest, 1000, _setting.ScanTimeoutRequestInterval);
        }
        private void StopScanTimeoutRequestTask()
        {
            _scheduleService.StopTask(string.Format("{0}.ScanTimeoutRequest", this.GetType().Name));
        }
        private void StartReconnectServerTask(bool ignoreConnected = false)
        {
            _scheduleService.StartTask(string.Format("{0}.ReconnectServer", this.GetType().Name), () => ReconnectServer(ignoreConnected), 1000, _setting.ReconnectToServerInterval);
        }
        private void StopReconnectServerTask()
        {
            _scheduleService.StopTask(string.Format("{0}.ReconnectServer", this.GetType().Name));
        }
        private void StartSendHeartbeatTask(bool ignoreConnected = false)
        {
            _scheduleService.StartTask(string.Format("{0}.SendHeartbeat", this.GetType().Name), SendHeartbeat, 1000, _setting.SendHeartbeatInterval);
        }
        private void StopSendHeartbeatTask()
        {
            _scheduleService.StopTask(string.Format("{0}.SendHeartbeat", this.GetType().Name));
        }
        private void EnsureClientStatus()
        {
            if (_clientSocket == null || !_clientSocket.IsConnected)
            {
                throw new RemotingServerUnAvailableException(_serverEndPoint);
            }
        }
        private bool EnterReconnecting()
        {
            return Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0;
        }
        private void ExitReconnecting()
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
        private void SetLocalEndPoint(EndPoint localEndPoint)
        {
            _localEndPoint = localEndPoint;
        }
        private void SendHeartbeat()
        {
            try
            {
                InvokeAsync(new RemotingRequest(_setting.HeartbeatRequestCode, HeartbeatMessage), _setting.HeartbeatResponseTimeoutMilliseconds)
                    .ContinueWith(t =>
                    {
                        if (!t.IsCompleted || t.IsFaulted || t.Exception != null || t.Result == null || t.Result.ResponseCode == 0)
                        {
                            _heartbeatFailedCount++;
                            if (_heartbeatFailedCount >= 3)
                            {
                                _heartbeatFailedCount = 0;
                                _logger.Error("Socket client heartbeat failed for three times, start the reconnect server task.");
                                StartReconnectServerTask(true);
                            }
                        }
                    }).ConfigureAwait(false);
            }
            catch
            {
                _heartbeatFailedCount++;
                if (_heartbeatFailedCount >= 3)
                {
                    _heartbeatFailedCount = 0;
                    _logger.Error("Socket client heartbeat failed for three times, start the reconnect server task.");
                    StartReconnectServerTask(true);
                }
            }
        }

        class ConnectionEventListener : IConnectionEventListener
        {
            private readonly SocketRemotingClient _remotingClient;

            public ConnectionEventListener(SocketRemotingClient remotingClient)
            {
                _remotingClient = remotingClient;
            }

            public void OnConnectionAccepted(ITcpConnection connection) { }
            public void OnConnectionEstablished(ITcpConnection connection)
            {
                _remotingClient.StopReconnectServerTask();
                _remotingClient.ExitReconnecting();
                _remotingClient.SetLocalEndPoint(connection.LocalEndPoint);
            }
            public void OnConnectionFailed(EndPoint remotingEndPoint, SocketError socketError)
            {
                if (_remotingClient._shutteddown) return;

                _remotingClient.ExitReconnecting();
                _remotingClient.StartReconnectServerTask();
            }
            public void OnConnectionClosed(ITcpConnection connection, SocketError socketError)
            {
                if (_remotingClient._shutteddown) return;

                _remotingClient.ExitReconnecting();
                _remotingClient.StartReconnectServerTask();
            }
        }
    }
}
