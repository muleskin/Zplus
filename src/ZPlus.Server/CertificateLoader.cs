using System.Security.Cryptography.X509Certificates;

namespace ZPlus.Server;

/// <summary>
/// Loads the Kestrel HTTPS certificate. Two forms are supported:
///   • a PKCS#12 (.pfx/.p12) file protected by a password; or
///   • a PEM certificate file plus a separate PEM private-key file (Let's Encrypt style:
///     fullchain.pem + privkey.pem), with an optional passphrase for an encrypted key.
/// Which form is used is decided by whether a key path is supplied.
/// </summary>
internal static class CertificateLoader
{
    public static X509Certificate2 Load(string certPath, string? keyPath, string? password)
    {
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            var cert = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(certPath, password, keyPath);
            return MakeUsable(cert);
        }
        return LoadPkcs12FromFile(certPath, password);
    }

    // A cert built from PEM carries an ephemeral key that SChannel/Kestrel can't use on
    // Windows; a PFX export/import round-trip materialises a usable key. No-op elsewhere.
    private static X509Certificate2 MakeUsable(X509Certificate2 cert)
    {
        if (!OperatingSystem.IsWindows()) return cert;
        try
        {
            var pfx = cert.Export(X509ContentType.Pfx);
            return LoadPkcs12FromBytes(pfx, null);
        }
        finally
        {
            cert.Dispose();
        }
    }

    private static X509Certificate2 LoadPkcs12FromFile(string path, string? password)
    {
#if NET9_0_OR_GREATER
        // The X509Certificate2 file constructors are obsolete from .NET 9 onward.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
#else
        return new X509Certificate2(path, password);
#endif
    }

    private static X509Certificate2 LoadPkcs12FromBytes(byte[] bytes, string? password)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(bytes, password);
#else
        return new X509Certificate2(bytes, password);
#endif
    }
}
