using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

Console.WriteLine("SslStream Echo Server");
Console.WriteLine("Args: " + string.Join(" ", args));
var ip = args[0] == "localhost" ? IPAddress.Loopback : IPAddress.Parse(args[0]);
var port = int.Parse(args[1]);

using var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
server.Bind(new IPEndPoint(ip, port));

server.Listen();

var sslOptions = new SslServerAuthenticationOptions
{
    ApplicationProtocols = [new SslApplicationProtocol("test")],
    ServerCertificate = CreateSelfSignedCertificate(),
};

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

    requestRef.Request = HandleRequestAsync(requestRef.Id, accept, sslOptions);
}

// ---

async Task HandleRequestAsync(long id, Socket socket, SslServerAuthenticationOptions options)
{
    byte[]? buffer = null;
    byte[]? lengthBuffer = null;
    try
    {
        using var sslStream = await EstablishServerSslStreamAsync(socket, options);

        buffer = ArrayPool<byte>.Shared.Rent(64*1024);
        lengthBuffer = ArrayPool<byte>.Shared.Rent(sizeof(int));

        await ReadExactAsync(sslStream, lengthBuffer, sizeof(int));
        var lengthToRead = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

        await ReadExactAsync(sslStream, buffer, lengthToRead);

        // echo
        await sslStream.WriteAsync(lengthBuffer.AsMemory(0, sizeof(int)));
        await sslStream.WriteAsync(buffer.AsMemory(0, lengthToRead));
        await sslStream.FlushAsync();
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

X509Certificate2 CreateSelfSignedCertificate()
{
    using var rsa = RSA.Create();
    var certReq = new CertificateRequest("CN=contoso.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
    certReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
    if (OperatingSystem.IsWindows())
    {
        cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
    return cert;
}

async Task<SslStream> EstablishServerSslStreamAsync(Socket socket, SslServerAuthenticationOptions options)
{
    var networkStream = new NetworkStream(socket, ownsSocket: true);
    var stream = new SslStream(networkStream, leaveInnerStreamOpen: false);
    await stream.AuthenticateAsServerAsync(options, default);
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

record RequestRef(long Id)
{
    public Task? Request { get; set; }
}
