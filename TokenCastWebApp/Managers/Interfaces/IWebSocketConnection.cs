using System.Threading.Tasks;
using System;

namespace TokenCastWebApp.Managers.Interfaces
{
    public interface IWebSocketConnection : IDisposable, IEquatable<IWebSocketConnection>
    {
        string ConnectionId { get; }

        string DeviceId { get; }
        
        string Address { get; }

        void Send(byte[] message);

        Task StartReceiveMessageAsync();
    }
}
