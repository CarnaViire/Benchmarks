using System.Net;
using System.Net.Sockets;
using Microsoft.Crank.EventSources;

Console.WriteLine("Socket HelloWorld Client");
Console.WriteLine("Args: " + string.Join(" ", args));

// Parse arguments (easy way to do it would be via System.CommandLine package).
var ip = args[0] == "localhost" ? IPAddress.Loopback : IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);

// Register events for crank to collect.
// It doesn't really matter what you put in Operations because we are going to report each
// measurement only once -- it is better to do all post-processing locally,
// otherwise spamming events could overshadow the actual perf we're measuring.
BenchmarksEventSource.Register("socket/sent", Operations.Sum, Operations.Sum, "Bytes sent", "Bytes sent", "n0");
BenchmarksEventSource.Register("socket/recv", Operations.Sum, Operations.Sum, "Bytes received", "Bytes received", "n0");

var bytes = "Hello, World!"u8.ToArray();
var buffer = new byte[bytes.Length + 1];

// ---

// In a real benchmark, there needs to be a warmup period before we start collecting measurements.
// But this is just a hello-world, not a real benchmark -- we are not collecting anything meaningful,
// so there's no point in warming up, looping etc.
using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
await client.ConnectAsync(new IPEndPoint(ip, port));

// I find it useful to output some logs to console to be able to check the progress.
Console.WriteLine("Connected");

await client.SendAsync(bytes, SocketFlags.None);
client.Shutdown(SocketShutdown.Send);

var total = 0;
while (total < buffer.Length)
{
    var recv = await client.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None);
    if (recv == 0)
    {
        break;
    }
    total += recv;
}
client.Dispose();

// ---

// Report measurements to crank. I find it useful to output them to console as well.
BenchmarksEventSource.Measure("socket/sent", bytes.Length);
Console.WriteLine("socket/sent: " + bytes.Length);

BenchmarksEventSource.Measure("socket/recv", total);
Console.WriteLine("socket/recv: " + total);
