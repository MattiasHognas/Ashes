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

    private TlsLoopbackTestHost(string tempDirectory, X509Certificate2 serverCertificate, string trustCertificatePath, string untrustedCertificatePath)
    {
        this.tempDirectory = tempDirectory;
        ServerCertificate = serverCertificate;
        TrustCertificatePath = trustCertificatePath;
        UntrustedCertificatePath = untrustedCertificatePath;
    }

    public X509Certificate2 ServerCertificate { get; }

    public string TrustCertificatePath { get; }

    /// <summary>
    /// Path to a PEM file containing a different, unrelated self-signed root CA. When supplied
    /// to the runtime via SSL_CERT_FILE this routes verification through the deterministic PEM
    /// verifier (instead of Wine's platform verifier) and yields a stable UnknownIssuer error
    /// for the server certificate produced by this host.
    /// </summary>
    public string UntrustedCertificatePath { get; }

    public static async Task<TlsLoopbackTestHost> CreateAsync(string hostName = "localhost")
    {
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset rootNotAfter = notBefore.AddDays(2);
        DateTimeOffset serverNotAfter = notBefore.AddDays(1);

        // ECDSA P-256 is used (instead of RSA-2048) because the loopback TLS fixture must
        // negotiate full TLS handshakes under qemu-aarch64 user-mode emulation on x64 CI hosts,
        // where RSA modular exponentiation is the dominant handshake cost. ECDSA P-256 signature
        // verification is an order of magnitude cheaper under emulation, which keeps the
        // compiled arm64 process well under the SocketTestConstants.ProcessExitTimeout budget
        // for the https-via-loopback tests. rustls supports ecdsa_secp256r1_sha256 natively.
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Ashes Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

        using X509Certificate2 rootCertificate = rootRequest.CreateSelfSigned(notBefore, rootNotAfter);

        using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest($"CN={hostName}", serverKey, HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
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

        // Generate a second, unrelated self-signed root CA and write its PEM. Tests that exercise
        // untrusted-certificate behavior point SSL_CERT_FILE at this file so verification goes
        // through the PEM verifier (deterministic UnknownIssuer) rather than the platform verifier.
        using ECDsa untrustedRootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var untrustedRootRequest = new CertificateRequest("CN=Ashes Test Untrusted Root", untrustedRootKey, HashAlgorithmName.SHA256);
        untrustedRootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        untrustedRootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        untrustedRootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(untrustedRootRequest.PublicKey, false));
        using X509Certificate2 untrustedRootCertificate = untrustedRootRequest.CreateSelfSigned(notBefore, rootNotAfter);
        string untrustedCertificatePath = Path.Combine(tempDirectory, "test-untrusted-root-ca.pem");
        await File.WriteAllTextAsync(untrustedCertificatePath, untrustedRootCertificate.ExportCertificatePem());

        return new TlsLoopbackTestHost(tempDirectory, serverCertificate, trustCertificatePath, untrustedCertificatePath);
    }

    public void Configure(ProcessStartInfo startInfo)
    {
        startInfo.Environment["SSL_CERT_FILE"] = TrustCertificatePath;
    }

    public static async Task<Exception?> RunServerAsync(
        TcpListener listener,
        int expectedClientCount,
        X509Certificate2 serverCertificate,
        Func<SslStream, Task> handleClientAsync,
        bool tolerateClientDisconnect = false)
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

                await Task.WhenAll(clients.Select(client => HandleClientAsync(client, serverCertificate, handleClientAsync, tolerateClientDisconnect)));
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
        TryDeleteFile(UntrustedCertificatePath);
        TryDeleteDirectory(tempDirectory);
    }

    private static async Task HandleClientAsync(TcpClient client, X509Certificate2 serverCertificate, Func<SslStream, Task> handleClientAsync, bool tolerateClientDisconnect = false)
    {
        using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await stream
                .AuthenticateAsServerAsync(serverCertificate, clientCertificateRequired: false, enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false)
                .WaitAsync(SocketTestConstants.TlsHandshakeTimeout);
            await handleClientAsync(stream).WaitAsync(SocketTestConstants.SocketTimeout);
            await stream.FlushAsync();
            await stream.ShutdownAsync().WaitAsync(SocketTestConstants.SocketTimeout);
        }
        catch (IOException) when (tolerateClientDisconnect)
        {
            // The client side may close its end before or during the server's TLS handshake,
            // writing, or shutdown (e.g. in race-style scenarios where the loser's connection
            // is abandoned). Treat such socket failures as benign when the caller opts in.
        }
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
