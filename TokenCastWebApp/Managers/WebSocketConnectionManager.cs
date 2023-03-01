using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System;
using TokenCastWebApp.Managers.Interfaces;
using TokenCastWebApp.Models;
using TokenCastWebApp.Socket;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using TokenCast;

namespace TokenCastWebApp.Managers
{
    public sealed class WebSocketConnectionManager : IWebSocketConnectionManager, IWebSocketHandler
    {
        #region Private members

        private readonly IMemoryCache _tempSessionIdCache;
        private readonly ILogger<IWebSocketConnectionManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly RealtimeOptions _realtimeOptions;
        private readonly ISystemTextJsonSerializer _serializer;
        private readonly IDatabase _database;

        private readonly ConcurrentDictionary<string, IWebSocketConnection> _webSockets =
            new ConcurrentDictionary<string, IWebSocketConnection>();

        private readonly ConcurrentDictionary<string, IWebSocketConnection> _uiWebSockets =
            new ConcurrentDictionary<string, IWebSocketConnection>();

        #endregion

        #region Constructor

        public WebSocketConnectionManager(ILogger<IWebSocketConnectionManager> logger,
            ILoggerFactory loggerFactory,
            IOptions<RealtimeOptions> realtimeOptions,
            ISystemTextJsonSerializer serializer,
            IDatabase database)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _realtimeOptions = realtimeOptions.Value;
            _serializer = serializer;

            var cacheOptions = new MemoryCacheOptions
                { ExpirationScanFrequency = TimeSpan.FromSeconds(_realtimeOptions.ExpirationScanFrequency) };
            _tempSessionIdCache = new MemoryCache(cacheOptions, _loggerFactory);
            _database = database;
        }

        #endregion

        #region IWebSocketConnectionManager members

        public async Task<string> GenerateConnectionId(string deviceId)
        {
            if (deviceId == null) return null;

            var accounts = await _database.GetAllAccount();
            var exists = accounts.Any(a => a.devices.Contains(deviceId));

            if (exists)
            {
                return Guid.NewGuid().ToString();
            }

            return null;
        }

        public bool TryGetDeviceId(string connectionId, out string deviceId)
        {
            var exist = _tempSessionIdCache.TryGetValue(connectionId, out deviceId);

            if (exist)
                _tempSessionIdCache.Remove(connectionId);

            return exist;
        }

        public async Task ConnectAsync(string connectionId, string deviceId, WebSocket webSocket,
            CancellationToken cancellationToken)
        {
            var webSocketConnection = new WebSocketConnection(connectionId, deviceId, null, webSocket,
                cancellationToken, _loggerFactory, this, _serializer);

            _logger.LogInformation($"New WebSocket session {webSocketConnection.ConnectionId} connected.");

            _webSockets.TryAdd(deviceId, webSocketConnection);
            // _statusWebSocketConnectionManager.SendMessage(deviceId, new ClientMessageResponse
            // {
            //     Event = EventType.Online,
            //     Message = "Device is online",
            //     Success = true
            // });

            SendMessageToUI(deviceId, new ClientMessageResponse
            {
                Event = EventType.Online,
                Message = "Device is online",
                Success = true
            });
            await webSocketConnection.StartReceiveMessageAsync().ConfigureAwait(false);
        }

        private void SendMessageToUI(string deviceId, ClientMessageResponse message)
        {
            message.DeviceId = deviceId;
            var accounts = _database.GetAllAccount().GetAwaiter().GetResult().Where(x => x.devices.Contains(deviceId))
                .ToArray();

            foreach (var account in accounts)
            {
                if (_uiWebSockets.TryGetValue(account.address.ToUpperInvariant(), out var webSocketConnection))
                {
                    webSocketConnection.Send(ConvertMessageToBytes(message));
                }
            }
        }

        public async Task ConnectUIAsync(string connectionId, string address, WebSocket webSocket,
            CancellationToken cancellationToken)
        {
            var webSocketConnection = new WebSocketConnection(connectionId, null, address, webSocket, cancellationToken,
                _loggerFactory, this, _serializer);

            _logger.LogInformation($"New WebSocket session {webSocketConnection.ConnectionId} connected.");

            _uiWebSockets.TryAdd(address.ToUpperInvariant(), webSocketConnection);
            // _statusWebSocketConnectionManager.SendMessage(deviceId, new ClientMessageResponse
            // {
            //     Event = EventType.Online,
            //     Message = "Device is online",
            //     Success = true
            // });
            await webSocketConnection.StartReceiveMessageAsync().ConfigureAwait(false);
        }

        #endregion

        #region IWebSocketHandler members

        public void HandleHeartbeat(IWebSocketConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.DeviceId))
            {
                SendMessageToUI(connection.DeviceId, new ClientMessageResponse
                {
                    Event = EventType.Online,
                    Message = "Device is online",
                    Success = true
                });
            }
        }

        public void HandleDisconnection(IWebSocketConnection connection)
        {
            if (!string.IsNullOrWhiteSpace(connection.DeviceId))
            {
                SendMessageToUI(connection.DeviceId, new ClientMessageResponse
                {
                    Event = EventType.Offline,
                    Message = "Device is offline",
                    Success = true
                });
                _webSockets.TryRemove(connection.DeviceId, out _);
            }

            if (!string.IsNullOrWhiteSpace(connection.Address))
            {
                _uiWebSockets.TryRemove(connection.Address.ToUpper(), out _);
            }
        }

        public void HandleMessage(SocketClientMessage message)
        {
            var response = new ClientMessageResponse();
            try
            {
                _logger.LogInformation($"Start handle socket client message.");
                var subscribeMessage = ConvertBytesToMessage(message.Payload);

                if (subscribeMessage == default)
                {
                    response.Success = false;
                    response.Message = "Invalid request model";
                    return;
                }

                if (subscribeMessage.Action == SubscribeAction.UpdateNft)
                {
                    response.Event = EventType.NFTUpdated;
                    response.Message = "Event raised!";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                response.Success = false;
                response.Message = ex.Message;
            }
            finally
            {
                var bytes = ConvertMessageToBytes(response);
                message.Connection.Send(bytes);
            }
        }

        public void SendMessage(string deviceId, ClientMessageResponse message)
        {
            if (_webSockets.TryGetValue(deviceId, out var connection))
            {
                connection.Send(ConvertMessageToBytes(message));
            }
        }

        #endregion

        #region Private methods

        private byte[] ConvertMessageToBytes<T>(T message)
        {
            return _serializer.SerializeToBytes(message);
        }

        private ClientMessageRequest ConvertBytesToMessage(byte[] messageBytes)
        {
            try
            {
                var request = _serializer.Deserialize<ClientMessageRequest>(messageBytes);
                if (request == null)
                    throw new InvalidDataException("Invalid data");

                return request;
            }
            catch (JsonException ex)
            {
                return null;
            }
        }

        #endregion
    }
}