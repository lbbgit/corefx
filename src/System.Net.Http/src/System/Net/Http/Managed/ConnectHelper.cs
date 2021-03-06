﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal static class ConnectHelper
    {
        public static async ValueTask<Stream> ConnectAsync(HttpConnectionKey key, CancellationToken cancellationToken)
        {
            string host = key.Host;
            int port = key.Port;

            try
            {
                // Rather than creating a new Socket and calling ConnectAsync on it, we use the static
                // Socket.ConnectAsync with a SocketAsyncEventArgs, as we can then use Socket.CancelConnectAsync
                // to cancel it if needed.
                using (var saea = new BuilderAndCancellationTokenSocketAsyncEventArgs(cancellationToken))
                {
                    // Configure which server to which to connect.
                    saea.RemoteEndPoint = IPAddress.TryParse(host, out IPAddress address) ?
                        (EndPoint)new IPEndPoint(address, port) :
                        new DnsEndPoint(host, port);

                    // Hook up a callback that'll complete the Task when the operation completes.
                    saea.Completed += (s, e) =>
                    {
                        var csaea = (BuilderAndCancellationTokenSocketAsyncEventArgs)e;
                        switch (e.SocketError)
                        {
                            case SocketError.Success:
                                csaea.Builder.SetResult();
                                break;
                            case SocketError.OperationAborted:
                            case SocketError.ConnectionAborted:
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    csaea.Builder.SetException(new OperationCanceledException(csaea.CancellationToken));
                                    break;
                                }
                                goto default;
                            default:
                                csaea.Builder.SetException(new SocketException((int)e.SocketError));
                                break;
                        }
                    };

                    // Initiate the connection.
                    if (Socket.ConnectAsync(SocketType.Stream, ProtocolType.Tcp, saea))
                    {
                        // Connect completing asynchronously. Enable it to be canceled and wait for it.
                        using (cancellationToken.Register(s => Socket.CancelConnectAsync((SocketAsyncEventArgs)s), saea))
                        {
                            await saea.Builder.Task.ConfigureAwait(false);
                        }
                    }
                    else if (saea.SocketError != SocketError.Success)
                    {
                        // Connect completed synchronously but unsuccessfully.
                        throw new SocketException((int)saea.SocketError);
                    }

                    Debug.Assert(saea.SocketError == SocketError.Success, $"Expected Success, got {saea.SocketError}.");
                    Debug.Assert(saea.ConnectSocket != null, "Expected non-null socket");
                    Debug.Assert(saea.ConnectSocket.Connected, "Expected socket to be connected");

                    // Configure the socket and return a stream for it.
                    Socket socket = saea.ConnectSocket;
                    socket.NoDelay = true;
                    return new NetworkStream(socket, ownsSocket: true);
                }
            }
            catch (SocketException se)
            {
                throw new HttpRequestException(se.Message, se);
            }
        }

        /// <summary>SocketAsyncEventArgs that carries with it additional state for a Task builder and a CancellationToken.</summary>
        private sealed class BuilderAndCancellationTokenSocketAsyncEventArgs : SocketAsyncEventArgs
        {
            public AsyncTaskMethodBuilder Builder { get; }
            public CancellationToken CancellationToken { get; }

            public BuilderAndCancellationTokenSocketAsyncEventArgs(CancellationToken cancellationToken)
            {
                var b = new AsyncTaskMethodBuilder();
                var ignored = b.Task; // force initialization
                Builder = b;

                CancellationToken = cancellationToken;
            }
        }

        public static async ValueTask<SslStream> EstablishSslConnectionAsync(HttpConnectionSettings settings, string host, HttpRequestMessage request, Stream stream, CancellationToken cancellationToken)
        {
            RemoteCertificateValidationCallback callback = null;
            if (settings._serverCertificateCustomValidationCallback != null)
            {
                callback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
                {
                    try
                    {
                        return settings._serverCertificateCustomValidationCallback(request, certificate as X509Certificate2, chain, sslPolicyErrors);
                    }
                    catch (Exception e)
                    {
                        throw new HttpRequestException(SR.net_http_ssl_connection_failed, e);
                    }
                };
            }

            var sslStream = new SslStream(stream);

            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ClientCertificates = settings._clientCertificates,
                    EnabledSslProtocols = settings._sslProtocols,
                    CertificateRevocationCheckMode = settings._checkCertificateRevocationList ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = callback
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                sslStream.Dispose();
                if (e is AuthenticationException || e is IOException)
                {
                    throw new HttpRequestException(SR.net_http_ssl_connection_failed, e);
                }
                throw;
            }

            return sslStream;
        }
    }
}
