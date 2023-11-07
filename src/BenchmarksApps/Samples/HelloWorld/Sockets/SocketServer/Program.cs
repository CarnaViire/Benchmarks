using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

Console.WriteLine("Socket Echo Server");
Console.WriteLine("Args: " + string.Join(" ", args));
var ip = args[0] == "localhost" ? IPAddress.Loopback : IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);

using var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
server.Bind(new IPEndPoint(ip, port));

server.Listen();

Console.WriteLine("Server started");

// we need this to prevent GC from collecting unawaited tasks
var _requestRefs = new ConcurrentDictionary<long, RequestRef>();
var nextId = 0;

while (true)
{
    var accept = await server.AcceptAsync();
    accept.NoDelay = true;

    var requestRef = new RequestRef(nextId++);
    _requestRefs.TryAdd(requestRef.Id, requestRef);

    requestRef.Request = HandleRequestAsync(requestRef.Id, accept);
}

// ---

async Task HandleRequestAsync(long id, Socket socket)
{
    byte[]? buffer = null;
    try
    {
        buffer = ArrayPool<byte>.Shared.Rent(64*1024);
        var total = 0;
        while (total < buffer.Length)
        {
            var recv = await socket.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None);
            if (recv == 0)
            {
                break;
            }
            total += recv;
        }

        await socket.SendAsync(buffer.AsMemory(0, total), SocketFlags.None); // echo
    }
    finally
    {
        if (buffer != null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // the way Kestrel does it
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch { } // ignore
        socket.Dispose();

        _requestRefs.TryRemove(id, out _); // clean up when finished
    }
}

record RequestRef(long Id)
{
    public Task? Request { get; set; }
}