using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Crank.EventSources;
using System;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Linq;
using System.Collections.Generic;

Console.WriteLine(string.Join(" ", args));
var ip = IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);
var threads = int.Parse(args[2]);
Console.WriteLine($"ip={ip}; port={port}; threads={threads}");

PrintActiveTcpConnections(port, "start");

string query = $"GET / HTTP/1.1\r\nHost:{ip}:{port}\r\n\r\n";
byte[] request = Encoding.ASCII.GetBytes(query);

int W = 4 * 3_200 / threads;
int K = 4 * 64_000 / threads;

var tasks = new List<Task<double>>(threads);
for (int i = 0; i < threads; ++i)
{
    tasks.Add(MeasureRPS());
}

var threadRpsValues = await Task.WhenAll(tasks);

PrintActiveTcpConnections(port, "end");

double rps = 0;
foreach (var threadRpsValue in threadRpsValues)
{
    rps += threadRpsValue;
}

RegisterAndLog("http/rps/mean", "Mean RPS", rps);

// ---

async Task<double> MeasureRPS()
{
    byte[] receiveBuffer = new byte[64 * 1024];

    for (int i = 0; i < W; ++i)
    {
        await Request(receiveBuffer);
    }

    var stopwatch = Stopwatch.StartNew();
    for (int i = 0; i < K; ++i)
    {
        await Request(receiveBuffer);
    }
    var elapsedTicks = stopwatch.ElapsedTicks;

    var elapsed = elapsedTicks * 1.0 / Stopwatch.Frequency;
    return K * 1.0 / elapsed;
}

async Task Request(byte[] buf)
{
    Memory<byte> memory = buf;

    using Socket client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    await client.ConnectAsync(new IPEndPoint(ip, port));
    await client.SendAsync(request, SocketFlags.None);
    int total = 0;
    while (true)
    {
        int num = await client.ReceiveAsync(memory[total..], SocketFlags.None);
        if (num == 0)
        {
            break;
        }
        total += num;
    }
    client.Shutdown(SocketShutdown.Both);
    client.Dispose();
}


void PrintActiveTcpConnections(int port, string prefix)
{
    IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
    TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
    RegisterAndLog($"env/systemconnections/all/{prefix}", $"[{prefix}] Active TCP connections", connections.Length);
    RegisterAndLog($"env/systemconnections/port/{prefix}", $"[{prefix}] Active TCP connections to {port}", connections.Count(x => x.RemoteEndPoint.Port == port));
}

void RegisterAndLog(string name, string description, double value)
{
    BenchmarksEventSource.Register(name, Operations.Max, Operations.Max, description, description, "n0");
    BenchmarksEventSource.Measure(name, value);
    Console.WriteLine($"{description}: {value}");
}
