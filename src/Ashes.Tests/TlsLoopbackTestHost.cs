using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ashes.Tests;

internal sealed class TlsLoopbackTestHost : IDisposable
{
    private readonly string tempDirectory;

    private TlsLoopbackTestHost(string tempDirectory, X509Certificate2 serverCertificate, string trustCertificatePath)
    {
        this.tempDirectory = tempDirectory;
        ServerCertificate = serverCertificate;
        TrustCertificatePath = trustCertificatePath;
    }

    public X509Certificate2 ServerCertificate { get; }

    public string TrustCertificatePath { get; }

    public static async Task<TlsLoopbackTestHost> CreateAsync(string hostName = "localhost")
    {
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset rootNotAfter = notBefore.AddDays(2);
        DateTimeOffset serverNotAfter = notBefore.AddDays(1);

        using RSA rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Ashes Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

        using X509Certificate2 rootCertificate = rootRequest.CreateSelfSigned(notBefore, rootNotAfter);

        using RSA serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest($"CN={hostName}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var enhancedKeyUsage = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1", "Server Authentication")
        };
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsage, true));
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(hostName);
        serverRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());

        using X509Certificate2 serverCertificatePublic = serverRequest.Create(
            rootCertificate,
            notBefore,
            serverNotAfter,
            RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 serverCertificateWithKey = serverCertificatePublic.CopyWithPrivateKey(serverKey);
        byte[] serverCertificatePfx = serverCertificateWithKey.Export(X509ContentType.Pfx);
        var serverCertificate = X509CertificateLoader.LoadPkcs12(serverCertificatePfx, string.Empty, X509KeyStorageFlags.Exportable, Pkcs12LoaderLimits.Defaults);

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string trustCertificatePath = Path.Combine(tempDirectory, "test-root-ca.pem");
        await File.WriteAllTextAsync(trustCertificatePath, rootCertificate.ExportCertificatePem());

        return new TlsLoopbackTestHost(tempDirectory, serverCertificate, trustCertificatePath);
    }

    public void Configure(ProcessStartInfo startInfo)
    {
        startInfo.Environment["SSL_CERT_FILE"] = TrustCertificatePath;
    }

    public static async Task<Exception?> RunServerAsync(
        TcpListener listener,
        int expectedClientCount,
        X509Certificate2 serverCertificate,
        Func<SslStream, Task> handleClientAsync)
    {
        try
        {
            var clients = new List<TcpClient>(expectedClientCount);

            try
            {
                for (var index = 0; index < expectedClientCount; index++)
                {
                    using var acceptCts = new CancellationTokenSource(SocketTestConstants.AcceptTimeout);
                    var client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
                    clients.Add(client);
                }

                await Task.WhenAll(clients.Select(client => HandleClientAsync(client, serverCertificate, handleClientAsync)));
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        ServerCertificate.Dispose();
        TryDeleteFile(TrustCertificatePath);
        TryDeleteDirectory(tempDirectory);
    }

    private static async Task HandleClientAsync(TcpClient client, X509Certificate2 serverCertificate, Func<SslStream, Task> handleClientAsync)
    {
        using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await stream
            .AuthenticateAsServerAsync(serverCertificate, clientCertificateRequired: false, enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false)
            .WaitAsync(SocketTestConstants.TlsHandshakeTimeout);
        await handleClientAsync(stream).WaitAsync(SocketTestConstants.SocketTimeout);
        await stream.FlushAsync();
        await stream.ShutdownAsync().WaitAsync(SocketTestConstants.SocketTimeout);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
