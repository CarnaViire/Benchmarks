using System.Net;
using System.Net.Sockets;
using System.Text;
using System;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Linq;
using Microsoft.Crank.EventSources;
using System.Buffers;
using System.Collections.Generic;

Console.WriteLine(string.Join(" ", args));
var ip = IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);
Console.WriteLine($"ip={ip}; port={port}");

PrintActiveTcpConnections(port, "start");

string responseText = $"HTTP/1.1 200 OK\r\nContent-Length: 12\r\n\r\nHello World!";
byte[] response = Encoding.ASCII.GetBytes(responseText);

using Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
server.Bind(new IPEndPoint(ip, port));

server.Listen();

Console.WriteLine("Application started");

var tasksRoot = new List<Task>(1_000_000); // don't bother awaiting, but need to prevent GC

while (true)
{
    Socket accept = await server.AcceptAsync();
    accept.NoDelay = true;
    tasksRoot.Add(HandleRequest(accept));
}

// ---

async Task HandleRequest(Socket socket)
{
    byte[]? receiveBuffer = null;
    try
    {
        receiveBuffer = ArrayPool<byte>.Shared.Rent(64*1024);
        Memory<byte> memory = receiveBuffer;
        int total = 0;
        while (true)
        {
            int num = await socket.ReceiveAsync(memory[total..], SocketFlags.None);
            total += num;
            if (num == 0)
            {
                Console.WriteLine("Unexpected EOS");
                throw new Exception("Unexpected EOS");
            }
            if (total >= 4 &&
                receiveBuffer[total - 4] == (byte)'\r' &&
                receiveBuffer[total - 3] == (byte)'\n' &&
                receiveBuffer[total - 2] == (byte)'\r' &&
                receiveBuffer[total - 1] == (byte)'\n')
            {
                break;
            }
        }

        await socket.SendAsync(response, SocketFlags.None);
    }
    finally
    {
        if (receiveBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
        // the way Kestrel does it
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        { 
            // ignore 
        }
        socket.Dispose();
    }
}


void PrintActiveTcpConnections(int port, string prefix)
{
    IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
    TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
    RegisterAndLog($"env/systemconnections/all/{prefix}", $"[{prefix}] Active TCP connections", connections.Length);
    RegisterAndLog($"env/systemconnections/port/{prefix}", $"[{prefix}] Active TCP connections from {port}", connections.Count(x => x.LocalEndPoint.Port == port));
}

void RegisterAndLog(string name, string description, int value)
{
    BenchmarksEventSource.Register(name, Operations.Max, Operations.Max, description, description, "n0");
    BenchmarksEventSource.Measure(name, value);
    Console.WriteLine($"{description}: {value}");
}