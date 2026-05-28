/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using C64Emulator.Core;

namespace C64Emulator.Network
{
    /// <summary>
    /// Wraps C64Net TCP sockets in a mandatory TLS stream.
    /// </summary>
    /// <remarks>
    /// The emulator is designed for simple LAN sessions, so the host automatically
    /// creates a long-lived self-signed certificate in the user's AppData directory.
    /// Clients use trust-on-first-use fingerprint pinning: the first certificate seen
    /// for a host/port pair is stored, and a later certificate change is rejected.
    /// This keeps setup friction low while still preventing silent replacement after
    /// the initial trust decision.
    /// </remarks>
    public static class C64NetTls
    {
        private const string ServerCertificateFileName = "network-server.pfx";
        private const string TrustedCertificatesFileName = "network-trust.json";
        private const string ServerCertificateSubject = "CN=C64EmulatorNetworkServer";
        private const string ServerCertificateDnsName = "C64EmulatorNetworkServer";
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
        private const int CertificateKeySizeBits = 2048;
        private static readonly SslProtocols AllowedProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        private static readonly object CertificateSyncRoot = new object();
        private static readonly object TrustSyncRoot = new object();
        private static X509Certificate2 _serverCertificate;

        /// <summary>
        /// Authenticates an accepted server-side socket as TLS before C64Net framing begins.
        /// </summary>
        /// <param name="tcpClient">Accepted TCP client.</param>
        /// <returns>Authenticated TLS stream carrying all later C64Net messages.</returns>
        public static SslStream AuthenticateServer(TcpClient tcpClient)
        {
            if (tcpClient == null)
            {
                throw new ArgumentNullException(nameof(tcpClient));
            }

            var stream = new SslStream(tcpClient.GetStream(), false);
            try
            {
                stream.AuthenticateAsServer(LoadOrCreateServerCertificate(), false, AllowedProtocols, false);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Authenticates a client-side socket and validates the server certificate pin.
        /// </summary>
        /// <param name="tcpClient">Connected TCP client.</param>
        /// <param name="host">Host name or IP entered by the user.</param>
        /// <param name="port">Remote TCP port.</param>
        /// <param name="status">Short status text describing the TLS trust decision.</param>
        /// <returns>Authenticated TLS stream carrying all later C64Net messages.</returns>
        public static SslStream AuthenticateClient(TcpClient tcpClient, string host, int port, out string status)
        {
            if (tcpClient == null)
            {
                throw new ArgumentNullException(nameof(tcpClient));
            }

            string validationStatus = "TLS CONNECTING";
            var stream = new SslStream(
                tcpClient.GetStream(),
                false,
                (sender, certificate, chain, errors) => ValidateServerCertificate(host, port, certificate, out validationStatus));

            try
            {
                stream.AuthenticateAsClient(ServerCertificateDnsName, null, AllowedProtocols, false);
                status = validationStatus;
                return stream;
            }
            catch (AuthenticationException ex)
            {
                stream.Dispose();
                string failureStatus = string.Equals(validationStatus, "TLS CONNECTING", StringComparison.Ordinal)
                    ? "TLS FAILED"
                    : validationStatus;
                throw new C64NetTlsException(failureStatus, ex);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Returns the SHA-256 fingerprint of the local server certificate for display/logging.
        /// </summary>
        /// <returns>Uppercase SHA-256 certificate fingerprint.</returns>
        public static string GetServerCertificateFingerprint()
        {
            return ComputeFingerprint(LoadOrCreateServerCertificate());
        }

        /// <summary>
        /// Returns the short display form of the local server certificate fingerprint.
        /// </summary>
        /// <returns>Grouped fingerprint prefix suitable for overlays.</returns>
        public static string GetServerCertificateShortFingerprint()
        {
            return FormatShortFingerprint(GetServerCertificateFingerprint());
        }

        /// <summary>
        /// Returns the stored short fingerprint for a previously trusted server.
        /// </summary>
        /// <param name="host">Host name or IP entered by the user.</param>
        /// <param name="port">Remote TCP port.</param>
        /// <returns>Grouped fingerprint prefix, or UNKNOWN when no pin exists yet.</returns>
        public static string GetTrustedServerShortFingerprint(string host, int port)
        {
            string trustKey = BuildTrustKey(host, port);
            lock (TrustSyncRoot)
            {
                NetworkTrustFile trustFile = LoadTrustFile();
                if (trustFile.PinnedCertificates.TryGetValue(trustKey, out string fingerprint))
                {
                    return FormatShortFingerprint(fingerprint);
                }
            }

            return "UNKNOWN";
        }

        /// <summary>
        /// Loads the persisted server certificate or creates a new self-signed one.
        /// </summary>
        /// <returns>Certificate with private key for TLS server authentication.</returns>
        private static X509Certificate2 LoadOrCreateServerCertificate()
        {
            lock (CertificateSyncRoot)
            {
                if (_serverCertificate != null && _serverCertificate.HasPrivateKey)
                {
                    return _serverCertificate;
                }

                string path = GetServerCertificatePath();
                try
                {
                    if (File.Exists(path))
                    {
                        var certificate = X509CertificateLoader.LoadPkcs12(
                            File.ReadAllBytes(path),
                            (string)null,
                            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable,
                            null);
                        if (certificate.HasPrivateKey && certificate.NotAfter > DateTime.UtcNow.AddDays(7))
                        {
                            _serverCertificate = certificate;
                            return _serverCertificate;
                        }

                        certificate.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }

                _serverCertificate = CreateServerCertificate();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, _serverCertificate.Export(X509ContentType.Pfx));
                return _serverCertificate;
            }
        }

        /// <summary>
        /// Creates a self-signed certificate suitable for TLS server authentication.
        /// </summary>
        /// <returns>New certificate with exportable private key.</returns>
        private static X509Certificate2 CreateServerCertificate()
        {
            using (RSA rsa = RSA.Create(CertificateKeySizeBits))
            {
                var request = new CertificateRequest(
                    ServerCertificateSubject,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    false));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid(ServerAuthOid) },
                    false));

                var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
                subjectAlternativeNames.AddDnsName(ServerCertificateDnsName);
                request.CertificateExtensions.Add(subjectAlternativeNames.Build());

                using (X509Certificate2 temporaryCertificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(10)))
                {
                    return X509CertificateLoader.LoadPkcs12(
                        temporaryCertificate.Export(X509ContentType.Pfx),
                        (string)null,
                        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable,
                        null);
                }
            }
        }

        /// <summary>
        /// Validates the server certificate by SHA-256 fingerprint pin.
        /// </summary>
        /// <param name="host">Host name or IP entered by the user.</param>
        /// <param name="port">Remote TCP port.</param>
        /// <param name="certificate">Certificate supplied by the TLS server.</param>
        /// <param name="status">Short status text describing the decision.</param>
        /// <returns>True when the certificate is trusted for this host/port pair.</returns>
        private static bool ValidateServerCertificate(string host, int port, X509Certificate certificate, out string status)
        {
            if (certificate == null)
            {
                status = "TLS NO CERT";
                return false;
            }

            string fingerprint;
            using (X509Certificate2 serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()))
            {
                fingerprint = ComputeFingerprint(serverCertificate);
            }
            string trustKey = BuildTrustKey(host, port);
            lock (TrustSyncRoot)
            {
                NetworkTrustFile trustFile = LoadTrustFile();
                if (trustFile.PinnedCertificates.TryGetValue(trustKey, out string pinnedFingerprint))
                {
                    if (string.Equals(pinnedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        status = "TLS PIN OK " + FormatShortFingerprint(fingerprint);
                        return true;
                    }

                    status = "TLS CERT CHANGED";
                    return false;
                }

                trustFile.PinnedCertificates[trustKey] = fingerprint;
                SaveTrustFile(trustFile);
                status = "TLS PINNED " + FormatShortFingerprint(fingerprint);
                return true;
            }
        }

        /// <summary>
        /// Computes a certificate SHA-256 fingerprint.
        /// </summary>
        /// <param name="certificate">Certificate to hash.</param>
        /// <returns>Uppercase hexadecimal SHA-256 fingerprint.</returns>
        private static string ComputeFingerprint(X509Certificate certificate)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(certificate.GetRawCertData());
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToUpperInvariant();
            }
        }

        /// <summary>
        /// Formats the first bytes of a fingerprint for compact overlay status messages.
        /// </summary>
        /// <param name="fingerprint">Full uppercase SHA-256 fingerprint.</param>
        /// <returns>Short grouped fingerprint prefix.</returns>
        private static string FormatShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length < 12)
            {
                return "UNKNOWN";
            }

            return fingerprint.Substring(0, 4) + "-" + fingerprint.Substring(4, 4) + "-" + fingerprint.Substring(8, 4);
        }

        /// <summary>
        /// Builds the persistent trust key for a target host and port.
        /// </summary>
        /// <param name="host">Host name or IP entered by the user.</param>
        /// <param name="port">Remote TCP port.</param>
        /// <returns>Stable lower-case trust key.</returns>
        private static string BuildTrustKey(string host, int port)
        {
            string normalizedHost = string.IsNullOrWhiteSpace(host) ? "unknown" : host.Trim().ToLowerInvariant();
            return normalizedHost + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Loads the trust-on-first-use certificate pin file.
        /// </summary>
        /// <returns>Trust file with a non-null pin dictionary.</returns>
        private static NetworkTrustFile LoadTrustFile()
        {
            string path = GetTrustedCertificatesPath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    NetworkTrustFile trustFile = JsonSerializer.Deserialize<NetworkTrustFile>(json);
                    if (trustFile != null)
                    {
                        trustFile.EnsureInitialized();
                        return trustFile;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }

            return new NetworkTrustFile();
        }

        /// <summary>
        /// Saves the trust-on-first-use certificate pin file.
        /// </summary>
        /// <param name="trustFile">Trust file to persist.</param>
        private static void SaveTrustFile(NetworkTrustFile trustFile)
        {
            string path = GetTrustedCertificatesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(trustFile, options));
        }

        /// <summary>
        /// Gets the server certificate PFX path.
        /// </summary>
        /// <returns>Absolute AppData certificate path.</returns>
        private static string GetServerCertificatePath()
        {
            return Path.Combine(UserDataPaths.GetBaseDirectory(), ServerCertificateFileName);
        }

        /// <summary>
        /// Gets the pinned remote certificate trust file path.
        /// </summary>
        /// <returns>Absolute AppData trust file path.</returns>
        private static string GetTrustedCertificatesPath()
        {
            return Path.Combine(UserDataPaths.GetBaseDirectory(), TrustedCertificatesFileName);
        }

        /// <summary>
        /// JSON-serialized trust-on-first-use pin storage.
        /// </summary>
        private sealed class NetworkTrustFile
        {
            /// <summary>
            /// Gets or sets the storage format version.
            /// </summary>
            public int Version { get; set; } = 1;

            /// <summary>
            /// Gets or sets host:port to SHA-256 certificate fingerprint mappings.
            /// </summary>
            public Dictionary<string, string> PinnedCertificates { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Makes sure deserialized older files have all required collections.
            /// </summary>
            public void EnsureInitialized()
            {
                if (PinnedCertificates == null)
                {
                    PinnedCertificates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }
    }

    /// <summary>
    /// Represents a user-visible TLS setup or validation failure.
    /// </summary>
    public sealed class C64NetTlsException : Exception
    {
        /// <summary>
        /// Initializes a TLS exception with status text and inner failure.
        /// </summary>
        /// <param name="message">Short user-facing status.</param>
        /// <param name="innerException">Underlying TLS exception.</param>
        public C64NetTlsException(string message, Exception innerException)
            : base(string.IsNullOrWhiteSpace(message) ? "TLS FAILED" : message, innerException)
        {
        }
    }
}
