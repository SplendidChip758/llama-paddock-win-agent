using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LlamaPaddock.Agent;

/// <summary>
/// The agent's own ECDSA key and a self-signed certificate for it. The dashboard pins the key
/// (SHA-256 of its SPKI), not the certificate: the certificate never expires, and making a new one
/// from the same key needs no new pin. Keys made by the Node agent (PKCS#8 PEM) load unchanged.
/// </summary>
public static class Tls
{
    public sealed record Identity(X509Certificate2 Certificate, string Fingerprint);

    public static Identity Load(string keyPath, string certPath, ILogger log)
    {
        string? keyPem = ReadOrNull(keyPath);
        if (keyPem is null)
        {
            using var made = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            keyPem = made.ExportPkcs8PrivateKeyPem();
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            // CreateNew: never overwrite a key another start made meanwhile
            using (var f = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(f)) w.Write(keyPem);
            log.LogInformation("created a TLS key in {Path}", keyPath);
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(keyPem);

        string? certPem = ReadOrNull(certPath);
        if (certPem is null)
        {
            certPem = SelfSigned(key).ExportCertificatePem();
            using var f = new FileStream(certPath, FileMode.CreateNew, FileAccess.Write);
            using var w = new StreamWriter(f);
            w.Write(certPem);
        }

        using var pemCert = X509Certificate2.CreateFromPem(certPem, keyPem);
        // SChannel can't use the ephemeral key CreateFromPem gives; a PKCS#12 round trip stores it
        var cert = X509CertificateLoader.LoadPkcs12(pemCert.Export(X509ContentType.Pkcs12), null);
        return new Identity(cert, Fingerprint(cert));
    }

    /// <summary>SHA256:&lt;base64&gt; of the SPKI, without padding: the same as the Node and Linux agents.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).TrimEnd('=');

    static X509Certificate2 SelfSigned(ECDsa key)
    {
        var req = new CertificateRequest("CN=llama-paddock-agent", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        // 9999-12-31: no expiry (RFC 5280, 4.1.2.5)
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    static string? ReadOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
