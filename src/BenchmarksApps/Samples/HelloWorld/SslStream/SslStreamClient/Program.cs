using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Crank.EventSources;

Console.WriteLine("SslStream HelloWorld Client");
Console.WriteLine("Args: " + string.Join(" ", args));

// Parse arguments (easy way to do it would be via System.CommandLine package).
var ip = args[0] == "localhost" ? IPAddress.Loopback : IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);

// Register events for crank to collect.
// It doesn't really matter what you put in Operations because we are going to report each
// measurement only once -- it is better to do all post-processing locally,
// otherwise spamming events could overshadow the actual perf we're measuring.

BenchmarksEventSource.Register("sslstream/sent", Operations.First, Operations.First, "Bytes sent", "Bytes sent", "n0");
BenchmarksEventSource.Register("sslstream/recv", Operations.First, Operations.First, "Bytes received", "Bytes received", "n0");

var bytes = "Hello World"u8.ToArray();
var lengthBytes = new byte[sizeof(int)];
var buffer = new byte[bytes.Length + 1];

// ---

// In a real benchmark, there needs to be a warmup period before we start collecting measurements.
// But this is just a hello-world, not a real benchmark -- we are not collecting anything meaningful,
// so there's no point in warming up, looping etc.

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(ip, port));

using var sslStream = await EstablishClientSslStreamAsync(socket);

// I find it useful to output some logs to console to be able to check the progress.
Console.WriteLine("Connected");

BinaryPrimitives.WriteInt32BigEndian(lengthBytes, bytes.Length);
await sslStream.WriteAsync(lengthBytes);
await sslStream.WriteAsync(bytes);
await sslStream.FlushAsync();

await ReadExactAsync(sslStream, lengthBytes, sizeof(int));
var lengthToRead = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
await ReadExactAsync(sslStream, buffer, lengthToRead);

sslStream.Dispose();

// ---

// Report measurements to crank. I find it useful to output them to console as well.
BenchmarksEventSource.Measure("sslstream/sent", bytes.Length + sizeof(int));
Console.WriteLine($"sslstream/sent: {bytes.Length + sizeof(int)}");

BenchmarksEventSource.Measure("sslstream/recv", lengthToRead + sizeof(int));
Console.WriteLine($"sslstream/recv: {lengthToRead + sizeof(int)}");


// ---

async Task<SslStream> EstablishClientSslStreamAsync(Socket socket)
{
    var networkStream = new NetworkStream(socket, ownsSocket: true);

    var stream = new SslStream(networkStream, leaveInnerStreamOpen: false);
    var clientOptions = new SslClientAuthenticationOptions
    {
        ApplicationProtocols = [new SslApplicationProtocol("test")],
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
        TargetHost = "contoso.com",
    };

    await stream.AuthenticateAsClientAsync(clientOptions, default);

    return stream;
}

async Task ReadExactAsync(SslStream stream, Memory<byte> memory, int exactLength)
{
    if (exactLength > memory.Length)
    {
        throw new Exception($"Can't read {exactLength} b into Memory of {memory.Length} b");
    }

    var total = 0;
    while (total < exactLength)
    {
        var recv = await stream.ReadAsync(memory[total..]);
        if (recv == 0)
        {
            throw new Exception("Unexpected EOS");
        }
        total += recv;
    }
}
