using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using TokenCastWebApp.Models;

namespace TokenCastWebApp.Managers.Interfaces
{
    public interface IWebSocketConnectionManager
    {
        Task<string> GenerateConnectionId(string deviceId);

        bool TryGetDeviceId(string connectionId, out string deviceId);

        Task ConnectAsync(string connectionId, string deviceId, WebSocket webSocket, CancellationToken cancellationToken);
        Task ConnectUIAsync(string connectionId, string address, WebSocket webSocket, CancellationToken cancellationToken);

        void SendMessage(string deviceId, ClientMessageResponse message);
    }
}
